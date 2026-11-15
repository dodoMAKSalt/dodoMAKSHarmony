using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Меньше мусора → реже сборка мусора (~1 с фриза каждая).
    ///
    /// TimerTracker.Update (~19 штук, каждый кадр) создавал ~67 КБ мусора в секунду: каждый кадр брал список из пула,
    /// гонял Slinq и возвращал список — даже когда таймеров нет вовсе (почти всегда).
    /// Теперь: нет таймеров — выходим сразу; есть — простой цикл с общим списком. Поведение то же.
    /// </summary>
    internal static class GcFixes
    {
        private static System.Reflection.FieldInfo _timers;
        private static AccessTools.FieldRef<object, float> _actionTime;
        private static readonly List<GameObject> Due = new List<GameObject>();

        public static void Apply(Harmony h)
        {
            try
            {
                var evType = AccessTools.Inner(typeof(TimerTracker), "EventTime");
                _timers = AccessTools.Field(typeof(TimerTracker), "timers");
                _actionTime = AccessTools.FieldRefAccess<float>(evType, "actionTime");
                h.Patch(AccessTools.Method(typeof(TimerTracker), "Update"),
                    prefix: new HarmonyMethod(typeof(GcFixes), nameof(TimerUpdatePrefix)) { priority = Priority.Last });
                Plugin.Log.LogInfo("Мусор: TimerTracker без лишних выделений памяти.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Мусор: TimerTracker не исправлен: " + e.Message); }
        }

        private static bool TimerUpdatePrefix(TimerTracker __instance)
        {
            System.Collections.IDictionary timers;
            try { timers = _timers.GetValue(__instance) as System.Collections.IDictionary; } catch { return true; }
            if (timers == null) return true;
            if (timers.Count == 0) return false; // почти всегда — ничего не делаем и не мусорим

            // Есть таймеры: делаем то же, что оригинал, но без пула/Slinq.
            float now = Time.time;
            Due.Clear();
            foreach (System.Collections.DictionaryEntry kv in timers)
            {
                if (now >= _actionTime(kv.Value)) Due.Add((GameObject)kv.Key);
            }
            if (Due.Count == 0) return false;
            foreach (var go in Due)
            {
                if (go != null) __instance.TimerReached.Invoke(go);
            }
            foreach (var go in Due) timers.Remove(go);
            return false;
        }
    }
}
