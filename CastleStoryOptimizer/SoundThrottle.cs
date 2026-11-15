using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Звуковые объекты Wwise (AkGameObj) каждый кадр сверяют свою позицию с движком звука — у каждого хранилища,
    /// постройки, факела. Неподвижным (без Rigidbody, не бриктрон, не камера, не светлячок) достаточно делать это
    /// раз в 15 кадров (~0.12 с): звук остаётся на месте, а сотни лишних вызовов в кадр исчезают.
    /// Двигающиеся объекты обновляются как раньше, каждый кадр.
    /// </summary>
    internal static class SoundThrottle
    {
        private const int Every = 15;
        private static readonly Dictionary<int, bool> Slow = new Dictionary<int, bool>();
        private static int _frame = -1;

        public static void Apply(Harmony h)
        {
            try
            {
                var t = AccessTools.TypeByName("AkGameObj");
                h.Patch(AccessTools.Method(t, "Update"),
                    prefix: new HarmonyMethod(typeof(SoundThrottle), nameof(UpdatePrefix)) { priority = Priority.Last });
                Plugin.Log.LogInfo("Звук: неподвижные звуковые объекты обновляются раз в " + Every + " кадров.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Звук: не применено: " + e.Message); }
        }

        private static bool UpdatePrefix(Component __instance)
        {
            int f = Time.frameCount;
            if (f != _frame)
            {
                _frame = f;
                if (Slow.Count > 20000) Slow.Clear(); // защита от разрастания (объекты из пула)
            }
            int id = __instance.GetInstanceID();
            bool slow;
            if (!Slow.TryGetValue(id, out slow))
            {
                slow = IsStatic(__instance);
                Slow[id] = slow;
            }
            if (!slow) return true;
            return ((f + (id & 0x7fffffff)) % Every) == 0;
        }

        private static bool IsStatic(Component c)
        {
            try
            {
                if (c.GetComponentInParent<Rigidbody>() != null) return false;
                if (c.GetComponentInParent<Camera>() != null) return false;
                if (c.GetComponentInParent<CharacterController>() != null) return false;
                if (c.GetComponentInParent<Brix.Game.AI.Labor>() != null) return false;
                if (c.GetComponentInParent<Brix.Game.Components.Firefly>() != null) return false;
                if (c.GetComponentInParent<Animator>() != null) return false; // анимированные части (катапульты, двери) — по-старому
                return true;
            }
            catch { return false; }
        }
    }
}
