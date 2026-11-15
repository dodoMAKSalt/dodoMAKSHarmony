using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine.Nature;
using Brix.Game.AI;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Ускорение ИИ бриктронов (процессор и мусор).
    ///
    /// 1) HarvestGoalProvider.GoalCountOfType — «сколько деревьев/камней можно добыть». Вызывается у КАЖДОГО бриктрона
    ///    при КАЖДОМ выборе работы (приоритет задачи + «есть ли работа»), по каждой из ~12 задач добычи.
    ///    Оригинал перебирает все цели через Slinq и Enum.HasFlag: в Mono 2.x HasFlag упаковывает значения
    ///    (мусор) и работает через рефлексию. Заменяем на простой цикл с битовой проверкой + кэш на один кадр.
    ///    Результат тот же: (accepted &amp; type) == type — ровно то, что делает HasFlag.
    ///
    /// 2) Подробный замер внутренностей выбора работы (при DetailedHooks) — чтобы следующие правки делать по цифрам.
    /// </summary>
    internal static class AiPerf
    {
        private static AccessTools.FieldRef<HarvestGoalProvider, Dictionary<Brix.Engine.XYZ, NatureHarvestGoal>> _goals;

        private struct CountCache { public int Frame; public int Type; public int Count; public int Size; }
        private static readonly Dictionary<HarvestGoalProvider, CountCache> Cache = new Dictionary<HarvestGoalProvider, CountCache>();
        private static int _cacheFrame = -1;

        public static void Apply(Harmony h)
        {
            try
            {
                _goals = AccessTools.FieldRefAccess<HarvestGoalProvider, Dictionary<Brix.Engine.XYZ, NatureHarvestGoal>>("_goals");
                h.Patch(AccessTools.Method(typeof(HarvestGoalProvider), nameof(HarvestGoalProvider.GoalCountOfType)),
                    prefix: new HarmonyMethod(typeof(AiPerf), nameof(GoalCountPrefix)) { priority = Priority.Last });
                Plugin.Log.LogInfo("ИИ: быстрый подсчёт целей добычи (без HasFlag/Slinq, кэш на кадр).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("ИИ: подсчёт целей добычи не ускорен: " + e.Message); }
            try
            {
                _db = AccessTools.FieldRefAccess<ProjectDatabase, Dictionary<Type, HashSet<IOperable<Labor>>>>("_projectDatabase");
                h.Patch(AccessTools.Method(typeof(ProjectDatabase), nameof(ProjectDatabase.AvailableProjects)),
                    prefix: new HarmonyMethod(typeof(AiPerf), nameof(AvailablePrefix)) { priority = Priority.Last });
                Plugin.Log.LogInfo("ИИ: список доступных задач собирается без Slinq/делегатов.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("ИИ: список задач не ускорен: " + e.Message); }
        }

        private static AccessTools.FieldRef<ProjectDatabase, Dictionary<Type, HashSet<IOperable<Labor>>>> _db;

        /// <summary>То же, что оригинал (Projects().Where(Available).AddTo), но обычными циклами — без мусора.</summary>
        private static bool AvailablePrefix(ProjectDatabase __instance, List<IOperable<Labor>> projects)
        {
            Dictionary<Type, HashSet<IOperable<Labor>>> db;
            try { db = _db(__instance); } catch { return true; }
            if (db == null || projects == null) return true;
            foreach (var kv in db)
            {
                if (kv.Value == null) continue;
                foreach (var p in kv.Value)
                {
                    if (p == null) continue;
                    if (p.IsStillAlive && p.PriorityBias != ProjectPriority.Bias.Locked && !(p is IdleProject)) projects.Add(p);
                }
            }
            return false;
        }

        private static bool GoalCountPrefix(HarvestGoalProvider __instance, NatureType type, ref int __result)
        {
            Dictionary<Brix.Engine.XYZ, NatureHarvestGoal> goals;
            try { goals = _goals(__instance); } catch { return true; }
            if (goals == null) return true;

            int frame = Time.frameCount;
            if (frame != _cacheFrame) { Cache.Clear(); _cacheFrame = frame; }
            int t = (int)type;
            CountCache c;
            // кэш действителен в пределах кадра и пока не изменилось число целей
            if (Cache.TryGetValue(__instance, out c) && c.Type == t && c.Size == goals.Count) { __result = c.Count; return false; }

            int n = 0;
            foreach (var kv in goals) // Dictionary.Enumerator — структура, без мусора
            {
                int nt = (int)kv.Value.NatureType;
                if ((t & nt) == nt) n++;
            }
            Cache[__instance] = new CountCache { Frame = frame, Type = t, Count = n, Size = goals.Count };
            __result = n;
            return false;
        }

        /// <summary>Замер внутренностей выбора работы (только с DetailedHooks).</summary>
        public static void Profile(Harmony h)
        {
            int n = 0;
            Hook(h, typeof(Labor), "RefreshAvailableProjects", ref n);
            Hook(h, typeof(Labor), "TryWorkInProject", ref n);
            Hook(h, typeof(Labor), "SendProjectInstruction", ref n);
            Hook(h, typeof(Labor), "MaintainVoxelPresence", ref n);
            Hook(h, typeof(Labor), "AssignInstruction", ref n);
            Hook(h, typeof(ProjectDatabase), "AvailableProjects", ref n);
            Hook(h, typeof(HarvestGoalProvider), "GoalCountOfType", ref n);
            Hook(h, AccessTools.TypeByName("Brix.Game.AI.LaborInstinct"), "GetInstruction", ref n);
            Hook(h, AccessTools.TypeByName("Brix.Game.AI.KnowledgeSpace.KnowledgeUpdater"), "UpdateRegions", ref n);
            try
            {
                var m = AccessTools.Method(typeof(Labor), "ClosestResource",
                    new[] { typeof(GameObject).MakeByRefType(), typeof(int).MakeByRefType(), typeof(Description) });
                if (m != null && ProfilerPatches.TimeMethod(h, m, "[ИИ+] Labor.ClosestResource", "AI")) n++;
            }
            catch { }

            // Все «задачи» (IOperable<Labor>): есть ли работа / проверка / выдача приказа / приоритет
            string[] names = { "HasWorkFor", "VerifyWorkFor", "GetInstruction", "PriorityInput", "GoalCounts" };
            var iface = typeof(IOperable<Labor>);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (an != "Assembly-CSharp-firstpass" && an != "Assembly-CSharp") continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var t in types)
                {
                    if (t == null || t.IsInterface || t.ContainsGenericParameters || !iface.IsAssignableFrom(t)) continue;
                    foreach (var name in names)
                        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (m.Name != name || m.IsAbstract || m.GetMethodBody() == null) continue;
                            if (ProfilerPatches.TimeMethod(h, m, "[ИИ+] " + t.Name + "." + name, "AI")) n++;
                        }
                }
            }
            Plugin.Log.LogInfo("ИИ: подробный замер выбора работы, перехвачено " + n);
        }

        private static void Hook(Harmony h, Type t, string method, ref int n)
        {
            try
            {
                var m = t == null ? null : AccessTools.Method(t, method);
                if (m != null && ProfilerPatches.TimeMethod(h, m, "[ИИ+] " + t.Name + "." + method, "AI")) n++;
            }
            catch (Exception e) { Plugin.Log.LogDebug("ИИ замер " + method + ": " + e.Message); }
        }
    }
}
