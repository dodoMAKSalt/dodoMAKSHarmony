using System;
using System.Collections;
using HarmonyLib;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// GameAnalytics: api.gameanalytics.com v1 давно не отвечает. Игра всё равно
    /// копит события, шлёт запросы и пишет "GA Error" со стектрейсом в лог.
    /// Глушим очередь целиком.
    /// </summary>
    internal static class AnalyticsPatches
    {
        public static void Apply(Harmony h)
        {
            try
            {
                var skip = new HarmonyMethod(typeof(AnalyticsPatches), nameof(Skip));
                h.Patch(AccessTools.Method(typeof(GA_Queue), nameof(GA_Queue.AddItem)), prefix: skip);
                h.Patch(AccessTools.Method(typeof(GA_Queue), nameof(GA_Queue.ForceSubmit)), prefix: skip);
                h.Patch(AccessTools.Method(typeof(GA_Queue), nameof(GA_Queue.RestartSubmit)), prefix: skip);
                h.Patch(AccessTools.Method(typeof(GA_Queue), nameof(GA_Queue.SubmitQueue)),
                    prefix: new HarmonyMethod(typeof(AnalyticsPatches), nameof(EmptyCoroutine)));
                h.Patch(AccessTools.Method(typeof(GA_Submit), nameof(GA_Submit.SubmitQueue)), prefix: skip);
                Plugin.Log.LogInfo("GameAnalytics отключён.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Не удалось отключить GameAnalytics: " + e);
            }
        }

        private static bool Skip() => false;

        private static bool EmptyCoroutine(ref IEnumerator __result)
        {
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty()
        {
            yield break;
        }
    }
}
