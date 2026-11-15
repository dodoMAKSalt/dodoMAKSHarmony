using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Оптимизация мода BetterThanCastle (работает только если он установлен; наш мод без него работает как обычно).
    ///
    /// 1) Лишний проход GUILayout. Unity для каждого MonoBehaviour с OnGUI делает дополнительное событие Layout
    ///    (и мусор), если useGUILayout = true. Тем их компонентам, которые GUILayout не используют
    ///    (проверяем по IL их OnGUI и вызываемых им методов), выключаем его — рисуют они то же самое.
    /// 2) Подробный замер (с DetailedHooks): их подсистемы (…Tick/Poll), их перехваты методов игры
    ///    (Prefix/Postfix/Finalizer) — строки «[BTC] …» в отчёте, с мусором.
    /// </summary>
    internal static class BtcTuning
    {
        private static Assembly _asm;
        private static readonly List<Type> NoLayoutTypes = new List<Type>();
        private static float _nextScan;
        private static int _scans;
        private static readonly HashSet<int> Done = new HashSet<int>();

        public static void Apply(Harmony h, bool profile)
        {
            _asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "BetterThanCastle");
            if (_asm == null) return;
            FindNoLayoutTypes();
            Throttles(h);
            Spikes(h);
            if (profile) Profile(h);
        }

        // ---------- 4. Переборы сцены и мусор (пики) ----------
        private static FieldInfo _userByConn, _crystalNext;
        private static int _sceneHandle = int.MinValue;
        private static string _sceneKey;
        private static readonly Dictionary<string, string[]> KeyNames = new Dictionary<string, string[]>();
        private static readonly MethodInfo ConcatObj = AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(object), typeof(object) });

        private static void Spikes(Harmony h)
        {
            // a) статистика: FindObjectsOfType<User>() раз в секунду (~50 мс) → готовый список игроков игры
            try
            {
                var t = FindType("BtcMatchStatsNet");
                var m = t == null ? null : AccessTools.Method(t, "RefreshUsers");
                _userByConn = t == null ? null : AccessTools.Field(t, "UserByConn");
                if (m != null && _userByConn != null)
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(BtcTuning), nameof(RefreshUsersPrefix)) { priority = Priority.Last });
                    Plugin.Log.LogInfo("BetterThanCastle: поиск игроков для статистики без перебора сцены.");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("BetterThanCastle: поиск игроков не ускорен: " + e.Message); }

            // b) их сетевая диагностика: FindObjectsOfType<NetworkIdentity>() раз в 30 с (~40 мс) — только для их лога
            try
            {
                var t = FindType("BtcNetId");
                var scan = t == null ? null : AccessTools.Method(t, "Scan");
                if (scan != null) h.Patch(scan, prefix: new HarmonyMethod(typeof(BtcTuning), nameof(SkipPrefix)));
                var key = t == null ? null : AccessTools.Method(t, "GetSceneKey");
                if (key != null) h.Patch(key, prefix: new HarmonyMethod(typeof(BtcTuning), nameof(SceneKeyPrefix)));
                Plugin.Log.LogInfo("BetterThanCastle: сетевая диагностика без перебора сцены, ключ сцены без мусора.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("BetterThanCastle: сетевая диагностика: " + e.Message); }

            // c) Discord: счёт кристаллов (2 перебора сцены) раз в 6 с → раз в 60 с
            try
            {
                var t = FindType("BtcDiscord");
                var m = t == null ? null : AccessTools.Method(t, "CrystalScore");
                _crystalNext = t == null ? null : AccessTools.Field(t, "crystalNext");
                if (m != null && _crystalNext != null)
                    h.Patch(m, postfix: new HarmonyMethod(typeof(BtcTuning), nameof(CrystalScorePostfix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("BetterThanCastle: Discord: " + e.Message); }

            // d) хоткеи камеры/групп: "prefix_" + число каждый кадр → готовые строки
            try
            {
                var t = FindType("CameraBookmarkHotkey");
                int n = 0;
                if (t != null)
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null || !HasBoxConcat(m)) continue;
                        h.Patch(m, transpiler: new HarmonyMethod(typeof(BtcTuning), nameof(KeyNameTranspiler)));
                        n++;
                    }
                Plugin.Log.LogInfo("BetterThanCastle: хоткеи камеры без мусора (методов: " + n + ").");
            }
            catch (Exception e) { Plugin.Log.LogWarning("BetterThanCastle: хоткеи камеры: " + e.Message); }
        }

        private static bool RefreshUsersPrefix()
        {
            try
            {
                var dict = _userByConn.GetValue(null) as Dictionary<int, Brix.Game.User>;
                var mgr = Brix.Network.UNetManager.instance;
                if (dict == null || mgr == null || mgr.UserList == null) return true;
                dict.Clear();
                foreach (var u in mgr.UserList)
                {
                    if (u == null) continue;
                    var c = u.connectionToClient;
                    if (c != null) dict[c.connectionId] = u;
                }
                return false;
            }
            catch { return true; }
        }

        private static bool SkipPrefix() { return false; }

        private static bool SceneKeyPrefix(ref string __result)
        {
            var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (sc.GetHashCode() != _sceneHandle || _sceneKey == null)
            {
                _sceneHandle = sc.GetHashCode();
                _sceneKey = sc.buildIndex + ":" + sc.name;
            }
            __result = _sceneKey;
            return false;
        }

        private static void CrystalScorePostfix()
        {
            try
            {
                float next = (float)_crystalNext.GetValue(null);
                float want = Time.unscaledTime + 60f;
                if (next < want) _crystalNext.SetValue(null, want);
            }
            catch { }
        }

        private static readonly MethodInfo ConcatStr = AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string) });
        private static readonly MethodInfo IntToString = AccessTools.Method(typeof(int), nameof(int.ToString), Type.EmptyTypes);

        private static bool HasBoxConcat(MethodBase m)
        {
            var ins = PatchProcessor.ReadMethodBody(m).ToList();
            for (int i = 0; i + 1 < ins.Count; i++)
            {
                if (ins[i].Key == System.Reflection.Emit.OpCodes.Box && (ins[i].Value as Type) == typeof(int)
                    && ins[i + 1].Value as MethodInfo == ConcatObj) return true;
                // новый компилятор: ldloca tmp; call Int32.ToString(); call Concat(string, string)
                if (i > 0 && ins[i].Value as MethodInfo == IntToString && ins[i + 1].Value as MethodInfo == ConcatStr
                    && (ins[i - 1].Key == System.Reflection.Emit.OpCodes.Ldloca || ins[i - 1].Key == System.Reflection.Emit.OpCodes.Ldloca_S)) return true;
            }
            return false;
        }

        /// <summary>box int32; call string.Concat(object, object) → call KeyName(object, int): строка из кэша, без мусора.</summary>
        private static IEnumerable<CodeInstruction> KeyNameTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var keyName = AccessTools.Method(typeof(BtcTuning), nameof(KeyName));
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (list[i].opcode == System.Reflection.Emit.OpCodes.Box && (list[i].operand as Type) == typeof(int)
                    && list[i + 1].operand as MethodInfo == ConcatObj)
                {
                    list[i].opcode = System.Reflection.Emit.OpCodes.Nop;
                    list[i].operand = null;
                    list[i + 1].opcode = System.Reflection.Emit.OpCodes.Call;
                    list[i + 1].operand = keyName;
                }
                else if (i > 0 && list[i].operand as MethodInfo == IntToString && list[i + 1].operand as MethodInfo == ConcatStr
                    && (list[i - 1].opcode == System.Reflection.Emit.OpCodes.Ldloca || list[i - 1].opcode == System.Reflection.Emit.OpCodes.Ldloca_S))
                {
                    // адрес числа → само число; ToString убираем; Concat(string,string) → KeyName(object,int)
                    list[i - 1].opcode = list[i - 1].opcode == System.Reflection.Emit.OpCodes.Ldloca_S ? System.Reflection.Emit.OpCodes.Ldloc_S : System.Reflection.Emit.OpCodes.Ldloc;
                    list[i].opcode = System.Reflection.Emit.OpCodes.Nop;
                    list[i].operand = null;
                    list[i + 1].opcode = System.Reflection.Emit.OpCodes.Call;
                    list[i + 1].operand = keyName;
                }
            }
            return list;
        }

        public static string KeyName(object prefix, int n)
        {
            var p = prefix as string;
            if (p == null) return string.Concat(prefix, n);
            string[] arr;
            if (!KeyNames.TryGetValue(p, out arr)) KeyNames[p] = arr = new string[64];
            if (n < 0 || n >= arr.Length) return p + n;
            return arr[n] ?? (arr[n] = p + n);
        }

        // ---------- 3. Частые дорогие вызовы их мода ----------
        public static float StatsRecInterval = 1f;
        private static FieldInfo _recNext;
        private static float _pollNext;
        private static Func<string, KeyCode[], KeyCode[]> _getKeys;
        private static readonly string[] JobNames = new string[64];

        private static void Throttles(Harmony h)
        {
            // a) Запись статистики матча в реплей: 10 раз/с (каждая ~20–50 мс) → раз в StatsRecInterval
            try
            {
                var t = _asm.GetType("BetterThanCastle.BtcMatchStatsNet") ?? FindType("BtcMatchStatsNet");
                var rec = t == null ? null : AccessTools.Method(t, "RecTick");
                _recNext = t == null ? null : AccessTools.Field(t, "_recNext");
                if (rec != null && _recNext != null)
                {
                    h.Patch(rec, postfix: new HarmonyMethod(typeof(BtcTuning), nameof(RecTickPostfix)));
                    Plugin.Log.LogInfo("BetterThanCastle: статистика в реплей пишется раз в " + StatsRecInterval + " с (было 10 раз/с).");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("BetterThanCastle: статистика реплея не ускорена: " + e.Message); }

            // b) Проверка «после обновления»: File.Exists каждый кадр → раз в 2 с
            try
            {
                var t = FindType("BtcUpdater");
                var m = t == null ? null : AccessTools.Method(t, "PollPostUpdate");
                if (m != null)
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(BtcTuning), nameof(PollPrefix)));
                    Plugin.Log.LogInfo("BetterThanCastle: проверка обновления на диске раз в 2 с (было каждый кадр).");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("BetterThanCastle: проверка обновления не ускорена: " + e.Message); }

            // c) Хоткеи профессий: "job_" + job.ToString() 7 раз за кадр → строки один раз
            try
            {
                var t = FindType("BtcHotkeys");
                var gk = t == null ? null : AccessTools.Method(t, "GetKeys", new[] { typeof(string), typeof(KeyCode[]) });
                var gj = t == null ? null : AccessTools.Method(t, "GetJobKeys");
                if (gk != null && gj != null)
                {
                    _getKeys = (Func<string, KeyCode[], KeyCode[]>)Delegate.CreateDelegate(typeof(Func<string, KeyCode[], KeyCode[]>), gk);
                    h.Patch(gj, prefix: new HarmonyMethod(typeof(BtcTuning), nameof(JobKeysPrefix)));
                    Plugin.Log.LogInfo("BetterThanCastle: хоткеи профессий без лишних строк.");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("BetterThanCastle: хоткеи профессий не ускорены: " + e.Message); }
        }

        private static Type FindType(string name)
        {
            try { return _asm.GetTypes().FirstOrDefault(x => x.Name == name); }
            catch (ReflectionTypeLoadException e) { return e.Types.FirstOrDefault(x => x != null && x.Name == name); }
        }

        private static void RecTickPostfix()
        {
            try
            {
                float next = (float)_recNext.GetValue(null);
                float want = Time.unscaledTime + StatsRecInterval;
                if (next > Time.unscaledTime && next < want) _recNext.SetValue(null, want); // они поставили +0.1 с — растягиваем
            }
            catch { }
        }

        private static bool PollPrefix()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _pollNext) return false;
            _pollNext = now + 2f;
            return true;
        }

        private static bool JobKeysPrefix(Brix.Game.AI.Occupation.Job job, KeyCode[] fallback, ref KeyCode[] __result)
        {
            int i = (int)job;
            if (_getKeys == null || i < 0 || i >= JobNames.Length) return true;
            var n = JobNames[i] ?? (JobNames[i] = "job_" + job.ToString());
            __result = _getKeys(n, fallback);
            return false;
        }

        // ---------- 1. GUILayout ----------
        private static void FindNoLayoutTypes()
        {
            Type[] types;
            try { types = _asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
            {
                if (t == null || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                var ongui = t.GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (ongui == null) continue;
                bool uses;
                try { uses = UsesGuiLayout(ongui, 0, new HashSet<MethodBase>()); }
                catch { uses = true; } // не смогли проверить — не трогаем
                if (!uses) NoLayoutTypes.Add(t);
            }
            Plugin.Log.LogInfo("BetterThanCastle: без GUILayout рисуют " + NoLayoutTypes.Count + " компонент(ов): "
                + string.Join(", ", NoLayoutTypes.Select(x => x.Name).ToArray()));
        }

        /// <summary>Есть ли вызовы UnityEngine.GUILayout* в методе и (до 3 уровней) в вызываемых им методах их мода.</summary>
        private static bool UsesGuiLayout(MethodBase m, int depth, HashSet<MethodBase> seen)
        {
            if (m == null || !seen.Add(m) || m.GetMethodBody() == null) return false;
            foreach (var ins in PatchProcessor.ReadMethodBody(m))
            {
                var callee = ins.Value as MethodBase;
                if (callee == null) continue;
                var dt = callee.DeclaringType;
                if (dt != null && dt.Namespace == "UnityEngine" && dt.Name.StartsWith("GUILayout")) return true;
                if (dt == typeof(GUILayout) || dt == typeof(GUILayoutUtility)) return true;
                if (depth < 3 && dt != null && dt.Assembly == _asm && UsesGuiLayout(callee, depth + 1, seen)) return true;
            }
            return false;
        }

        /// <summary>Из Plugin.Update: раз в 5 с выключаем useGUILayout у новых экземпляров.</summary>
        public static void Tick()
        {
            if (_asm == null || NoLayoutTypes.Count == 0 || Time.realtimeSinceStartup < _nextScan) return;
            // FindObjectsOfType дорогой: их компоненты создаются один раз при старте — первые 6 проверок раз в 10 с, дальше раз в 5 мин
            _scans++;
            _nextScan = Time.realtimeSinceStartup + (_scans < 6 ? 10f : 300f);
            foreach (var t in NoLayoutTypes)
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(t))
                {
                    var mb = o as MonoBehaviour;
                    if (mb == null || !Done.Add(mb.GetInstanceID())) continue;
                    mb.useGUILayout = false;
                }
            }
        }

        // ---------- 2. Подробный замер ----------
        private static void Profile(Harmony h)
        {
            int n = 0;
            Type[] types;
            try { types = _asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            var names = new HashSet<string> { "Tick", "Poll", "PollPostUpdate", "LateTick" };
            foreach (var t in types)
            {
                if (t == null || t.ContainsGenericParameters) continue;
                bool isPatch = t.Namespace != null && t.Namespace.StartsWith("BetterThanCastle.Patches");
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                    bool want = (m.IsStatic && names.Contains(m.Name) && m.GetParameters().Length == 0)
                                || (isPatch && m.IsStatic && (m.Name == "Prefix" || m.Name == "Postfix" || m.Name == "Finalizer"));
                    if (!want) continue;
                    string label = isPatch ? "[BTC перехват] " + t.Name + "." + m.Name : "[BTC] " + t.Name + "." + m.Name;
                    try { if (ProfilerPatches.TimeMethod(h, m, label, "BTC")) n++; } catch { }
                }
            }
            Plugin.Log.LogInfo("BetterThanCastle: подробный замер — перехвачено " + n + " методов их мода.");
        }
    }
}
