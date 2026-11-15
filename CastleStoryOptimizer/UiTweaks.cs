using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Плашки над головами (OverheadDisplay, ~170 шт.): каждый кадр каждая плашка заново задаёт своему холсту
    /// sortingOrder = −расстояние до камеры. Присваивание помечает холсты для пересортировки — это работа на этапе
    /// «LateUpdate + перестройка UI». Теперь значение записывается, только если оно реально изменилось.
    /// </summary>
    internal static class UiTweaks
    {
        public static void Apply(Harmony h)
        {
            try
            {
                var t = AccessTools.TypeByName("Brix.NewUI.OverheadDisplay.OverheadDisplay");
                var m = t == null ? null : AccessTools.Method(t, "BrixUpdate");
                if (m == null) { Plugin.Log.LogWarning("Интерфейс: OverheadDisplay.BrixUpdate не найден."); return; }
                h.Patch(m, transpiler: new HarmonyMethod(typeof(UiTweaks), nameof(SortTranspiler)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Интерфейс: порядок плашек не исправлен: " + e.Message); }
        }

        private static IEnumerable<CodeInstruction> SortTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var setter = AccessTools.PropertySetter(typeof(Canvas), nameof(Canvas.sortingOrder));
            var ours = AccessTools.Method(typeof(UiTweaks), nameof(SetSortIfChanged));
            int n = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(setter)) { n++; yield return new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(ins); }
                else yield return ins;
            }
            Plugin.Log.LogInfo("Интерфейс: порядок плашек над головами пишется только при изменении (мест: " + n + ").");
        }

        public static void SetSortIfChanged(Canvas c, int order)
        {
            if (c != null && c.sortingOrder != order) c.sortingOrder = order;
        }
    }
}
