using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    internal sealed class Entry
    {
        public string Name;
        public string Category;
        public long Ticks;
        public long Calls;
        public long MaxTicks;
        public long FrameTicks; // за текущий кадр — для разбора фризов
        public long LoadTicks;  // за текущую загрузку катки
        public long LoadCalls;
        public long AllocBytes; // рост занятой кучи за время вызова (включительно, по блокам ~4 КБ — статистика)
    }

    /// <summary>
    /// Вешает замеры на:
    ///  * все Update/LateUpdate/FixedUpdate игровых MonoBehaviour;
    ///  * рабочие циклы поиска пути (SearchPathRequest, Knowledge*Finder);
    ///  * вызовы Lua через MoonSharp (Script.Call / Coroutine.Resume), без двойного счёта вложенных.
    /// </summary>
    internal static class ProfilerPatches
    {
        // Заполняется только при патчинге, дальше только чтение — безопасно из любых потоков.
        private static readonly Dictionary<MethodBase, Entry> Map = new Dictionary<MethodBase, Entry>();
        public static readonly List<Entry> All = new List<Entry>();

        [ThreadStatic] private static int _luaDepth;
        [ThreadStatic] private static int _pathDepth;

        // --- замер мусора: занятая куча до/после вызова (только главный поток) ---
        private static readonly long[] MemStack = new long[256];
        private static int _memDepth;
        private static int _mainThread = -1;
        public static long LastMem;

        /// <summary>F7: подробный замер вкл/выкл без перезапуска (выкл — перехваты почти ничего не делают).</summary>
        public static bool Active = true;
        /// <summary>Подробные перехваты реально установлены (на порте по умолчанию нет).</summary>
        public static bool Installed;
        /// <summary>Подсчёт мусора по методам (2 вызова GC.GetTotalMemory на каждый перехват — дорого). Конфиг TrackGarbage.</summary>
        public static bool TrackGarbage;

        internal static void MemPush()
        {
            if (!TrackGarbage) return;
            if (_mainThread < 0) _mainThread = Thread.CurrentThread.ManagedThreadId;
            else if (Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            if (_memDepth < MemStack.Length) MemStack[_memDepth] = GC.GetTotalMemory(false);
            _memDepth++;
        }

        internal static void MemPop(Entry e)
        {
            if (!TrackGarbage) return;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || _memDepth <= 0) return;
            _memDepth--;
            if (_memDepth >= MemStack.Length) return;
            long now = GC.GetTotalMemory(false);
            long d = now - MemStack[_memDepth];
            if (e != null && d > 0 && d < 64L * 1024 * 1024) e.AllocBytes += d; // отрицательное = в это время прошла сборка мусора
        }

        private static readonly string[] GameAssemblies =
        {
            "Assembly-CSharp-firstpass", "Assembly-CSharp", "Brix.Core", "brix-common",
            "Assembly-UnityScript", "Assembly-UnityScript-firstpass"
        };

        private static readonly string[] UpdateNames = { "Update", "LateUpdate", "FixedUpdate" };
        private static readonly string[] ClockNames = { "BrixUpdate", "BrixFixedUpdate", "BrixLateUpdate", "BrixSlowUpdate" };

        public static void Apply(Harmony h, bool detailed)
        {
            var sw = Stopwatch.StartNew();
            int ok = 0, fail = 0;

            // Кадр: «процессор занят» / «ожидание GPU, vsync, лимита FPS» (нужны перехваты Update для начала кадра)
            FrameSplit.Install(NewEntry("[Кадр] процессор занят", "Frame"), NewEntry("[Кадр] ожидание GPU / лимита FPS", "Frame"), detailed);
            if (detailed) {

            var timedPre = new HarmonyMethod(typeof(ProfilerPatches), nameof(TimedPrefix));
            var fixedPre = new HarmonyMethod(typeof(ProfilerPatches), nameof(FixedPrefix));
            var updPre = new HarmonyMethod(typeof(ProfilerPatches), nameof(UpdPrefix));
            var latePre = new HarmonyMethod(typeof(ProfilerPatches), nameof(LatePrefix));
            var fixedPost = new HarmonyMethod(typeof(ProfilerPatches), nameof(FixedPostfix));
            var timedPost = new HarmonyMethod(typeof(ProfilerPatches), nameof(TimedPostfix));

            // 1. Update-методы
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (Array.IndexOf(GameAssemblies, asm.GetName().Name) < 0) continue;
                foreach (var t in SafeTypes(asm))
                {
                    if (t == null || t.ContainsGenericParameters) continue;
                    if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    if (t.Name.StartsWith("GA_")) continue;
                    foreach (var n in UpdateNames)
                    {
                        var m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, Type.EmptyTypes, null);
                        if (m == null || m.IsAbstract || m.GetMethodBody() == null) continue;
                        var pre = n == "FixedUpdate" ? fixedPre : n == "Update" ? updPre : latePre;
                        var post = n == "FixedUpdate" ? fixedPost : timedPost;
                        if (Patch(h, m, t.Name + "." + n, n, pre, post)) ok++; else fail++;
                    }
                }
            }

            // 1а. Мод BetterThanCastle: его собственные Update/LateUpdate/FixedUpdate/OnGUI — строки «[BTC] …»
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "BetterThanCastle") continue;
                foreach (var t in SafeTypes(asm))
                {
                    if (t == null || t.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    foreach (var n in new[] { "Update", "LateUpdate", "FixedUpdate", "OnGUI" })
                    {
                        var m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, Type.EmptyTypes, null);
                        if (m == null || m.IsAbstract || m.GetMethodBody() == null) continue;
                        if (Patch(h, m, "[BTC] " + t.Name + "." + n, "BTC", timedPre, timedPost)) ok++; else fail++;
                    }
                }
            }

            // 1б. Подписчики GameClock (IUpdatableComponent.BrixUpdate и т.п.) — внутри GameClock.Update/FixedUpdate
            var clockIface = AccessTools.TypeByName("Brix.Game.Components.IUpdatableComponent");
            // 1в. Корутины: MoveNext сгенерированных итераторов, которые возвращают IEnumerator (не IEnumerable)
            int coroutines = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (Array.IndexOf(GameAssemblies, asm.GetName().Name) < 0) continue;
                foreach (var t in SafeTypes(asm))
                {
                    if (t == null || t.ContainsGenericParameters) continue;

                    if (clockIface != null && clockIface.IsAssignableFrom(t) && !t.IsInterface)
                    {
                        foreach (var n in ClockNames)
                        {
                            var m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                                null, Type.EmptyTypes, null);
                            if (m == null || m.IsAbstract || m.GetMethodBody() == null) continue;
                            if (m.GetMethodBody().GetILAsByteArray().Length <= 2) continue; // пустой метод
                            if (Patch(h, m, t.Name + "." + n, "Clock", timedPre, timedPost)) ok++; else fail++;
                        }
                    }

                    if (t.IsNested && t.Name.Contains("Iterator") && typeof(System.Collections.IEnumerator).IsAssignableFrom(t)
                        && !typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
                    {
                        var mn = t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (mn == null || mn.GetMethodBody() == null) continue;
                        string owner = t.DeclaringType != null ? t.DeclaringType.Name : "?";
                        string method = t.Name;
                        int lt = method.IndexOf('<'), gt = method.IndexOf('>');
                        if (lt >= 0 && gt > lt) method = method.Substring(lt + 1, gt - lt - 1);
                        if (Patch(h, mn, "(корутина) " + owner + "." + method, "Coroutine", timedPre, timedPost)) { ok++; coroutines++; } else fail++;
                    }
                }
            }
            Plugin.Log.LogInfo("Профайлер: корутин под замером " + coroutines);

            // 2. Поиск пути (вложенные вызовы считаются один раз)
            var pathPre = new HarmonyMethod(typeof(ProfilerPatches), nameof(PathPrefix));
            var pathPost = new HarmonyMethod(typeof(ProfilerPatches), nameof(PathPostfix));
            var pathSearch = NewEntry("[Пути] поиск юнитов (SearchPathRequest)", "Path");
            var pathKnow = NewEntry("[Пути] карта знаний (Knowledge*Finder)", "Path");
            PatchShared(h, "Brix.Game.AI.SearchPathRequest", "Work", pathSearch, pathPre, pathPost, ref ok, ref fail);
            PatchShared(h, "Brix.Game.AI.SearchPathRequest", "LoopImprovePath", pathSearch, pathPre, pathPost, ref ok, ref fail);
            PatchShared(h, "Brix.Game.AI.KnowledgeSpace.KnowledgeConnectedAccessFinder", "SearchStep", pathKnow, pathPre, pathPost, ref ok, ref fail);
            PatchShared(h, "Brix.Game.AI.KnowledgeSpace.KnowledgeDisjointRegionsFinder", "MapStep_Search", pathKnow, pathPre, pathPost, ref ok, ref fail);

            // 2б. Подозреваемые в разовых пиках (по замерам: LodUpdater.Update до 280 мс)
            var lodEntry = NewEntry("[Карта] LodUpdater: дальние уровни детализации", "Update");
            foreach (var spec in new[] {
                "Brix.Engine.FlatWorld.LodUpdater+LodBatchDrawer|GetPaddedLowerMvs",
                "Brix.Engine.FlatWorld.LodUpdater+LodBatchDrawer|InitLodData",
                "Brix.Engine.FlatWorld.LodUpdater+LodBatchDrawer|RenderLod",
                "Brix.Engine.FlatWorld.LodUpdater|RenderLod",
                "Brix.Engine.FlatWorld.LodUpdater|InitLodData",
                "Brix.Engine.FlatWorld.LodUpdater|GetPaddedLowerMvs" })
            {
                var parts = spec.Split('|');
                var tt = AccessTools.TypeByName(parts[0]);
                var mm = tt == null ? null : AccessTools.Method(tt, parts[1]);
                if (mm == null) continue;
                if (PatchWith(h, mm, NewEntry("[Карта] " + parts[0].Substring(parts[0].LastIndexOf('.') + 1) + "." + parts[1], "Update"), pathPre, pathPost)) ok++; else fail++;
            }
            PatchShared(h, "Brix.Engine.Jardinier", "Travailler", NewEntry("[Карта] Jardinier.Travailler (природа/LOD)", "Update"), pathPre, pathPost, ref ok, ref fail);

            // 3. Lua (MoonSharp) — все перегрузки Script.Call и Coroutine.Resume
            var luaPre = new HarmonyMethod(typeof(ProfilerPatches), nameof(LuaPrefix));
            var luaPost = new HarmonyMethod(typeof(ProfilerPatches), nameof(LuaPostfix));
            var lua = NewEntry("[Lua] MoonSharp, всего", "Lua");
            foreach (var typeName in new[] { "MoonSharp.Interpreter.Script", "MoonSharp.Interpreter.Coroutine" })
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) { Plugin.Log.LogWarning("Не найден тип " + typeName); continue; }
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "Call" && m.Name != "Resume") continue;
                    if (m.ContainsGenericParameters) continue;
                    if (PatchWith(h, m, lua, luaPre, luaPost)) ok++; else fail++;
                }
            }

            // 3б. Перестройка интерфейса Unity UI (layout + графика) — происходит после LateUpdate
            var canvasUpd = AccessTools.Method(AccessTools.TypeByName("UnityEngine.UI.CanvasUpdateRegistry"), "PerformUpdate");
            if (canvasUpd != null && PatchWith(h, canvasUpd, NewEntry("[UI] перестройка интерфейса (Canvas)", "UI"), timedPre, timedPost)) ok++;

            } // detailed

            // 4. Рендер (не патч, а колбэки камер)
            RenderTiming.Install(NewEntry("[Рендер] камеры, главный поток", "Render"));

            Plugin.Log.LogInfo(string.Format("Профайлер: пропатчено {0} методов, ошибок {1}, за {2} мс",
                ok, fail, sw.ElapsedMilliseconds));
        }

        /// <summary>Замерить произвольный метод (для других модулей).</summary>
        public static bool TimeMethod(Harmony h, MethodBase m, string name, string category)
        {
            if (m == null) return false;
            return PatchWith(h, m, NewEntry(name, category),
                new HarmonyMethod(typeof(ProfilerPatches), nameof(TimedPrefix)),
                new HarmonyMethod(typeof(ProfilerPatches), nameof(TimedPostfix)));
        }

        private static Entry NewEntry(string name, string category)
        {
            var e = new Entry { Name = name, Category = category };
            All.Add(e);
            return e;
        }

        private static bool Patch(Harmony h, MethodBase m, string name, string category, HarmonyMethod pre, HarmonyMethod post)
            => PatchWith(h, m, NewEntry(name, category), pre, post);

        private static void PatchShared(Harmony h, string type, string method, Entry e, HarmonyMethod pre, HarmonyMethod post, ref int ok, ref int fail)
        {
            var t = AccessTools.TypeByName(type);
            var m = t == null ? null : AccessTools.Method(t, method);
            if (m == null) { Plugin.Log.LogWarning("Не найден метод " + type + "." + method); fail++; return; }
            if (PatchWith(h, m, e, pre, post)) ok++; else fail++;
        }

        private static bool PatchWith(Harmony h, MethodBase m, Entry e, HarmonyMethod pre, HarmonyMethod post)
        {
            try
            {
                Map[m] = e;
                h.Patch(m, prefix: pre, postfix: post);
                return true;
            }
            catch (Exception ex)
            {
                Map.Remove(m);
                Plugin.Log.LogDebug("Пропуск " + m.DeclaringType + "." + m.Name + ": " + ex.Message);
                return false;
            }
        }

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null); }
        }

        private static void Record(MethodBase original, long start)
        {
            if (!Active) return;
            Entry e;
            if (!Map.TryGetValue(original, out e)) { MemPop(null); return; }
            MemPop(e);
            long dt = Stopwatch.GetTimestamp() - start;
            Interlocked.Add(ref e.Ticks, dt);
            Interlocked.Add(ref e.FrameTicks, dt);
            if (LoadingPatches.IsLoading) { e.LoadTicks += dt; e.LoadCalls++; }
            Interlocked.Increment(ref e.Calls);
            if (dt > e.MaxTicks) e.MaxTicks = dt; // гонка тут не страшна
        }

        // --- обычные методы ---
        private static void FixedPrefix(out long __state)
        {
            MemPush();
            __state = Stopwatch.GetTimestamp();
            if (FrameSplit.FrameFirst == 0) FrameSplit.FrameFirst = __state;
            FrameSplit.FixedSeen = true;
        }

        private static void UpdPrefix(out long __state)
        {
            MemPush();
            __state = Stopwatch.GetTimestamp();
            if (FrameSplit.FrameFirst == 0) FrameSplit.FrameFirst = __state;
            if (FrameSplit.FirstUpdate == 0) FrameSplit.FirstUpdate = __state;
        }

        private static void LatePrefix(out long __state)
        {
            MemPush();
            __state = Stopwatch.GetTimestamp();
            if (FrameSplit.FrameFirst == 0) FrameSplit.FrameFirst = __state;
            if (FrameSplit.FirstLate == 0) FrameSplit.FirstLate = __state;
        }

        private static void TimedPrefix(out long __state)
        {
            MemPush();
            __state = Stopwatch.GetTimestamp();
            if (FrameSplit.FrameFirst == 0) FrameSplit.FrameFirst = __state;
        }
        private static void TimedPostfix(MethodBase __originalMethod, long __state) => Record(__originalMethod, __state);

        // Время скриптов FixedUpdate за кадр — чтобы отделить их от самой физики
        private static void FixedPostfix(MethodBase __originalMethod, long __state)
        {
            FrameSplit.FixedScriptTicks += Stopwatch.GetTimestamp() - __state;
            Record(__originalMethod, __state);
        }

        // --- поиск пути ---
        private static void PathPrefix(out long __state)
        {
            if (_pathDepth++ == 0) { MemPush(); __state = Stopwatch.GetTimestamp(); } else __state = 0;
        }
        private static void PathPostfix(MethodBase __originalMethod, long __state)
        {
            if (--_pathDepth == 0 && __state != 0) Record(__originalMethod, __state);
        }

        // --- Lua ---
        private static void LuaPrefix(out long __state)
        {
            if (_luaDepth++ == 0) { MemPush(); __state = Stopwatch.GetTimestamp(); } else __state = 0;
        }
        private static void LuaPostfix(MethodBase __originalMethod, long __state, object[] __args)
        {
            if (--_luaDepth == 0 && __state != 0)
            {
                Record(__originalMethod, __state);
                long ms = (Stopwatch.GetTimestamp() - __state) * 1000 / Stopwatch.Frequency;
                if (ms >= SlowLuaMs) LogSlowLua(ms, __originalMethod, __args);
            }
        }

        // --- журнал медленных вызовов Lua (кто и что вызвал) ---
        private const int SlowLuaMs = 25;
        private static int _slowLuaLogged;

        private static void LogSlowLua(long ms, MethodBase m, object[] args)
        {
            if (_slowLuaLogged >= 150) return;
            _slowLuaLogged++;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss")).Append("  ").Append(ms).Append(" мс  ").Append(m.DeclaringType.Name).Append('.').Append(m.Name);
                if (args != null)
                    foreach (var a in args)
                    {
                        string t;
                        try { t = a == null ? "null" : a.ToString(); } catch { t = "?"; }
                        if (t.Length > 90) t = t.Substring(0, 90);
                        sb.Append("\n    арг: ").Append(t.Replace('\n', ' '));
                    }
                var st = new System.Diagnostics.StackTrace(2, false);
                int n = Math.Min(st.FrameCount, 14);
                for (int i = 0; i < n; i++)
                {
                    var f = st.GetFrame(i).GetMethod();
                    if (f == null) continue;
                    sb.Append("\n    ").Append(f.DeclaringType != null ? f.DeclaringType.Name : "?").Append("::").Append(f.Name);
                }
                sb.Append("\n");
                System.IO.File.AppendAllText(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CastleStoryOptimizer_slowlua.txt"), sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        // Если Lua/путь кинул исключение, postfix не вызовется и глубина «застрянет».
        // Сбрасываем раз в кадр с главного потока.
        public static void ResetDepthGuards()
        {
            _luaDepth = 0;
            _pathDepth = 0;
            _memDepth = 0; // вызывается из Plugin.Update — вне перехваченных методов
        }
    }
}
