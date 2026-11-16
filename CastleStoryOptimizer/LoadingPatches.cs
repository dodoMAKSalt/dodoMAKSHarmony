using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Загрузка мира (Brix.Engine.GameLoader) — это цепочка корутин, которые делают кусок
    /// работы и ждут конец кадра. При лимите 60 FPS каждый такой шаг стоит минимум 16,7 мс.
    ///
    /// FastLoading: на время загрузки снимаем лимит FPS и vsync, после — возвращаем как было.
    /// Плюс замер времени и количества кадров каждой фазы загрузки — пишется в лог и отчёт.
    /// </summary>
    internal static class LoadingPatches
    {
        private static readonly string[] Phases =
        {
            "Load", "BuildStep", "AwakeStep", "StartStep",
            "LoadBasicTerrain", "InitTerrainGravity", "LoadNature", "BuildTerrainMeshes",
            "LoadGameObjects", "LoadGridGuide", "InitPathfinding"
        };

        private static bool _fastLoading;
        private static int _savedTargetFps;
        private static int _savedVSync;
        private static readonly StringBuilder Log = new StringBuilder();
        private static int _depth;

        public static string LastLoadReport { get; private set; }
        public static bool IsLoading { get; private set; }
        private static int _loadStartFrame;

        public static void Apply(Harmony h, bool fastLoading, bool wrapPhases = true)
        {
            _fastLoading = fastLoading;
            var t = AccessTools.TypeByName("Brix.Engine.GameLoader");
            if (t == null) { Plugin.Log.LogWarning("GameLoader не найден — замер загрузки отключён."); return; }

            var post = new HarmonyMethod(typeof(LoadingPatches), nameof(WrapPostfix));
            int n = 0;
            foreach (var name in wrapPhases ? Phases : new string[0])
            {
                var m = AccessTools.Method(t, name);
                if (m == null || m.ReturnType != typeof(IEnumerator)) { Plugin.Log.LogWarning("Фаза загрузки не найдена: " + name); continue; }
                try { h.Patch(m, postfix: post); n++; }
                catch (Exception e) { Plugin.Log.LogWarning("Не удалось пропатчить " + name + ": " + e.Message); }
            }
            Plugin.Log.LogInfo("Загрузка: пропатчено фаз " + n + (fastLoading ? ", FastLoading включён" : ""));

            // Запасной путь (нужен с BetterThanCastle: обёртка фаз у нас тогда не срабатывала): начало — запуск загрузки
            // новой игры, конец — сигнал игры «уровень готов» / «ошибка загрузки». Повторные вызовы безопасны.
            var startM = AccessTools.Method(t, "LoadNewGameFromGameParams");
            if (startM != null)
            {
                try { h.Patch(startM, prefix: new HarmonyMethod(typeof(LoadingPatches), nameof(LoadNewGamePrefix))); }
                catch (Exception e) { Plugin.Log.LogWarning("Загрузка: запасной перехват не поставлен: " + e.Message); }
            }
        }

        private static void WrapPostfix(ref IEnumerator __result, MethodBase __originalMethod)
        {
            __result = Measure(__result, __originalMethod.Name);
        }

        private static IEnumerator Measure(IEnumerator inner, string name)
        {
            bool root = name == "Load";
            if (root && !IsLoading) BeginLoad();

            var sw = Stopwatch.StartNew();
            int startFrame = Time.frameCount;
            int myDepth = _depth++;
            try
            {
                while (inner.MoveNext())
                    yield return inner.Current;
            }
            finally
            {
                _depth = myDepth;
                sw.Stop();
                Log.AppendFormat("{0}{1,-20} {2,8:0} мс  {3,6} кадров\n",
                    new string(' ', myDepth * 2), name, sw.Elapsed.TotalMilliseconds, Time.frameCount - startFrame);
                if (root && IsLoading) EndLoad(sw.Elapsed.TotalSeconds);
            }
        }

        private static void LoadNewGamePrefix()
        {
            try
            {
                if (!IsLoading) BeginLoad();
                Brix.Engine.GameSignals.Connect(Brix.Engine.GameSignals.LevelReady, EndBySignal);
                Brix.Engine.GameSignals.Connect(Brix.Engine.GameSignals.LoadError, EndBySignal);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Загрузка: сигналы не подключены: " + e.Message); }
        }

        private static void EndBySignal()
        {
            if (IsLoading) EndLoad(Time.realtimeSinceStartup - _loadStartTime);
        }

        private static void BeginLoad()
        {
            Log.Length = 0;
            _depth = 0;
            IsLoading = true;
            _loadStartFrame = Time.frameCount;
            _loadStartTime = Time.realtimeSinceStartup;
            LoadingDiagnostics.ResetLoad();
            LuaCompileCache.Clear();
            StorageReservations.Clear();
            GcControl.Pause("загрузка мира");
            if (!_fastLoading) return;
            _savedTargetFps = Application.targetFrameRate;
            _savedVSync = QualitySettings.vSyncCount;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            Plugin.Log.LogInfo(string.Format("Загрузка мира: лимит FPS снят (было targetFPS={0}, vsync={1})", _savedTargetFps, _savedVSync));
        }

        public static bool CollectGarbageAfterLoad = true;
        private static float _loadStartTime;

        /// <summary>Страховка: если загрузка катки так и не «завершилась» (ошибка в игре), через 5 минут
        /// выходим из режима загрузки сами — иначе мод думает, что всё ещё грузимся (лимит FPS снят и т.п.).</summary>
        public static void Watchdog()
        {
            if (!IsLoading || Time.realtimeSinceStartup - _loadStartTime < 300f) return;
            Plugin.Log.LogWarning("Загрузка не завершилась за 5 минут (ошибка в игре?) — выходим из режима загрузки.");
            GcControl.Resume("загрузка зависла");
            IsLoading = false;
            if (_fastLoading)
            {
                Application.targetFrameRate = _savedTargetFps;
                QualitySettings.vSyncCount = _savedVSync;
            }
        }

        private static void EndLoad(double seconds)
        {
            GcControl.Resume("загрузка завершена"); // до GC.Collect: при выключенном сборщике он бы ничего не сделал
            // Уборка мусора, пока ещё экран загрузки: первый фриз сборщика в игре отодвигается на минуты.
            if (CollectGarbageAfterLoad)
            {
                var gcw = Stopwatch.StartNew();
                long before = GC.GetTotalMemory(false);
                GC.Collect();
                Plugin.Log.LogInfo(string.Format("Уборка мусора после загрузки: {0:0} мс, куча {1} → {2} МБ",
                    gcw.Elapsed.TotalMilliseconds, before / (1024 * 1024), GC.GetTotalMemory(false) / (1024 * 1024)));
            }
            IsLoading = false;
            if (_fastLoading)
            {
                Application.targetFrameRate = _savedTargetFps;
                QualitySettings.vSyncCount = _savedVSync;
            }
            // Фазы завершаются изнутри наружу — выводим в порядке завершения, с отступами по вложенности.
            LastLoadReport = string.Format("Загрузка мира: {0:0.0} с, {1} кадров{2}\n{3}\n{4}", seconds,
                Time.frameCount - _loadStartFrame, _fastLoading ? " (FastLoading)" : "", Log,
                LuaCompileCache.Summary() + "\n" + LoadingDiagnostics.LoadReport(Time.frameCount - _loadStartFrame));
            Plugin.Log.LogInfo(LastLoadReport);
            if (Plugin.Instance != null) Plugin.Instance.OnLoadFinished(LastLoadReport);
            StationPriorities.OnLevelLoaded();
            WorldSettings.OnLevelLoaded();
            AiStride.Reset();
            StepCap.OnLevelLoaded();
            NetPing.Register();
            AiFixes.OnLevelLoaded(Plugin.AiFastDecisions);
        }
    }
}
