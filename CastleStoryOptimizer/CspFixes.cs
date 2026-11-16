using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Исправления ошибок самого Castle Story Plus (их код не копируем и не правим — только патчи в памяти).
    /// 1) AutoCleanup.TryPlan: в списке предметов для уборки оставались уничтоженные объекты, сортировка по расстоянию
    ///    падала с NullReferenceException (Behavior error: Labor doing nothing), и бриктрон застывал с песочными часами.
    ///    Убираем уничтоженные объекты из списка кандидатов.
    /// </summary>
    internal static class CspFixes
    {
        private static int _removed;

        public static void Apply(Harmony h)
        {
            try
            {
                var t = AccessTools.TypeByName("CastleStoryPlus.WorkerAI.AutoCleanup");
                var m = t == null ? null : AccessTools.Method(t, "BaseCandidates");
                if (m == null) { Plugin.Log.LogWarning("Исправление AutoCleanup: метод BaseCandidates не найден."); return; }
                h.Patch(m, postfix: new HarmonyMethod(typeof(CspFixes), nameof(CandidatesPostfix)));
                Plugin.Log.LogInfo("Исправление AutoCleanup (застывшие бриктроны): установлено.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Исправление AutoCleanup: " + e.Message); }
        }

        private static void CandidatesPostfix(List<GameObject> __result)
        {
            if (__result == null) return;
            for (int i = __result.Count - 1; i >= 0; i--)
            {
                if (__result[i] == null) // перегруженное сравнение Unity: ловит и уничтоженные объекты
                {
                    __result.RemoveAt(i);
                    if (++_removed == 1 || _removed % 200 == 0)
                        Plugin.Log.LogInfo("AutoCleanup: убрано уничтоженных предметов из списка уборки: " + _removed);
                }
            }
        }
    }
}
