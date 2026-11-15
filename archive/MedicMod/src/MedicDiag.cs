using System;
using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Диагностика стройки медицинского стола: почему бриктрон не берётся за чертёж.
    /// Перехватываем BuildGoal.IsMatchFor только для нашего чертежа и пишем в лог причину отказа
    /// (не чаще раза в 10 с на причину) и состояние чертежа.
    /// </summary>
    internal static class MedicDiag
    {
        [ThreadStatic] private static bool _ours;
        private static readonly Dictionary<string, float> LastLog = new Dictionary<string, float>();

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(BuildGoal), nameof(BuildGoal.IsMatchFor)),
                    prefix: new HarmonyMethod(typeof(MedicDiag), nameof(MatchPrefix)),
                    postfix: new HarmonyMethod(typeof(MedicDiag), nameof(MatchPostfix)));
                h.Patch(AccessTools.Method(typeof(AIErrorsStore), "AddDecisionError"),
                    prefix: new HarmonyMethod(typeof(MedicDiag), nameof(ErrorPrefix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Медики: диагностика стройки не включена: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(ValidatorComponent), "Validate",
                        new[] { typeof(ValidationData).MakeByRefType(), typeof(System.Collections.Generic.IEnumerable<ValidationData>) }),
                    postfix: new HarmonyMethod(typeof(MedicDiag), nameof(ValidatePostfix)));
                h.Patch(AccessTools.Method(typeof(BuildGoalProvider), nameof(BuildGoalProvider.OnObjectTracked)),
                    postfix: new HarmonyMethod(typeof(MedicDiag), nameof(TrackedPostfix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Медики: диагностика проверки чертежа не включена: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(BuildGoalFinder), "Visit", new[] { typeof(Brix.Game.Components.Volumes.IVolume) }),
                    prefix: new HarmonyMethod(typeof(MedicDiag), nameof(VisitPrefix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Медики: диагностика поиска работы не включена: " + e.Message); }
        }

        private static void VisitPrefix(BuildGoalFinder __instance, Brix.Game.Components.Volumes.IVolume v)
        {
            try
            {
                var go = v == null ? null : v.GameObject;
                if (go == null) return;
                string nm = null;
                try { var imp = go.GetComponent<Brix.External.Factories.FactoryImprint>(); nm = imp != null ? imp.AssetKey.Name : null; } catch { }
                if (nm != "MedicalTable" && nm != "Workbench") return; // верстак — для сравнения
                var g = go.GetComponent<BuildGoal>();
                if (g == null) { Log("visit-nogoal", "Стол: поиск работы — у чертежа нет BuildGoal"); return; }
                var bp = g.Blueprint;
                var projT = AccessTools.Field(typeof(BuildGoalFinder), "ProjecTransform").GetValue(__instance) as Transform;
                var fetch = AccessTools.Field(typeof(BuildGoalFinder), "Fetchables").GetValue(__instance) as System.Collections.Generic.HashSet<Brix.External.Factories.Factory.AssetKey>;
                bool cap = fetch != null && g.recepteur != null && g.recepteur.HasBaseCapacityForOneOf(fetch);
                Log("visit" + nm + bp.State + g.Commited + (g.transform.parent == projT) + cap,
                    "[" + nm + "] поиск работы — флаги " + bp.State + ", подтверждён " + g.Commited +
                    ", в проекте " + (g.transform.parent == projT) + " (родитель " + (g.transform.parent != null ? g.transform.parent.name : "нет") +
                    ", проект " + (projT != null ? projT.name : "нет") + "), материалы подходят " + cap +
                    " (ищем: " + (fetch != null ? string.Join(",", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(fetch, k => k.Name))) : "—") + ")");
            }
            catch (Exception e) { Log("visit-err", "Стол: поиск работы — ошибка диагностики " + e.Message); }
        }

        private static bool IsOurGo(GameObject go)
        {
            try
            {
                var imp = go.GetComponent<Brix.External.Factories.FactoryImprint>();
                return imp != null && imp.AssetKey.Name == "MedicalTable";
            }
            catch { return false; }
        }

        private static void ValidatePostfix(ValidatorComponent __instance, ValidationResult __result)
        {
            if (__instance == null || !IsOurGo(__instance.gameObject)) return;
            string st = "?";
            try { var bp = __instance.GetComponent<Blueprint>(); st = bp != null ? bp.State.ToString() : "не чертёж"; } catch { }
            Log("val:" + __result + st, "Стол: проверка чертежа = " + __result + ", флаги = " + st +
                ", проверяющий = " + __instance.GetValidatorType.Name);
        }

        private static void TrackedPostfix(GameObject blueprintGO)
        {
            if (blueprintGO == null || !IsOurGo(blueprintGO)) return;
            string st = "?";
            try { var bpc = blueprintGO.GetComponent<Blueprint>(); st = bpc.State + ", подтверждён " + bpc.commited + ", родитель " + (blueprintGO.transform.parent != null ? blueprintGO.transform.parent.name : "нет"); } catch { }
            Log("tracked" + st, "Стол: чертёж взят в проект, флаги = " + st);
        }

        private static bool IsOurs(BuildGoal g)
        {
            try
            {
                var imp = g.GetComponent<Brix.External.Factories.FactoryImprint>();
                return imp != null && imp.AssetKey.Name == "MedicalTable";
            }
            catch { return false; }
        }

        private static void MatchPrefix(BuildGoal __instance) { _ours = IsOurs(__instance); }

        private static void MatchPostfix(BuildGoal __instance, Labor labor, bool __result)
        {
            if (!_ours) return;
            _ours = false;
            string state = "?";
            try
            {
                var bp = __instance.GetComponent<Blueprint>();
                var rec = __instance.GetComponent<Brix.Game.Semantique.Recepteur>();
                state = (bp != null ? bp.State.ToString() : "нет Blueprint") +
                        (rec != null ? " | нужно: " + rec.CurrentCapacity.GetPrintableDescription() : "");
            }
            catch (Exception e) { state = "ошибка: " + e.Message; }
            Log((__result ? "ПОДХОДИТ" : "не подходит") + " для " + (labor != null ? labor.name : "?"), "Стол (чертёж): " +
                (__result ? "бриктрон может строить" : "бриктрон НЕ может строить") + "; состояние: " + state);
        }

        private static void ErrorPrefix(AIError error)
        {
            if (!_ours || error == null) return;
            string what = error.thought != null ? error.thought.ToString() : error.GetType().Name;
            Log("err:" + what, "Стол (чертёж): причина отказа — " + what + " (приоритет " + error.priority + ")");
        }

        private static void Log(string key, string text)
        {
            float now = Time.realtimeSinceStartup;
            float last;
            if (LastLog.TryGetValue(key, out last) && now - last < 10f) return;
            LastLog[key] = now;
            Plugin.Log.LogInfo("Медики: " + text);
        }
    }
}
