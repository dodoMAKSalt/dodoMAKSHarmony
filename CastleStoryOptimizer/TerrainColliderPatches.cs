using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Brix.Engine.FlatWorld;
using HarmonyLib;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// ГЛАВНАЯ ПРИЧИНА ФРИЗОВ КАЖДЫЕ ~1.5 с (этап «физика», 50–160 мс).
    ///
    /// Рельеф получает физические коллайдеры «по требованию»:
    ///   ColliderSectorUpdater (сектор 14×14 чанков) — триггер; когда в него заходит юнит/предмет,
    ///     создаёт ~200 дочерних ColliderUpdater (каждый со своим триггером);
    ///   ColliderUpdater (чанк) — когда внутри кто-то есть, СТРОИТ коллайдер рельефа с нуля
    ///     (3× GetPixels32, пересчёт слоёв, сетка, cooking PhysX) — прямо в OnTriggerStay, внутри физики.
    /// И то и другое уничтожается через 1 секунду после того, как внутри никого нет.
    /// Бриктроны всё время ходят туда-сюда → коллайдеры строятся и уничтожаются без конца.
    ///
    /// Фикс: держим их не 1 с, а LingerSeconds (по умолчанию 20). Пока внутри кто-то есть,
    /// игра и раньше коллайдер не обновляла, так что «устаревание» не хуже оригинала.
    /// </summary>
    internal static class TerrainColliderPatches
    {
        private static float _linger = 20f;

        public static void Apply(Harmony h, float lingerSeconds)
        {
            _linger = Math.Max(1f, lingerSeconds);
            try
            {
                var tr = new HarmonyMethod(typeof(TerrainColliderPatches), nameof(LingerTranspiler));
                h.Patch(AccessTools.Method(typeof(ColliderUpdater), "OnTriggerStay"), transpiler: tr);
                h.Patch(AccessTools.Method(typeof(ColliderSectorUpdater), "OnTriggerStay"), transpiler: tr);
                Plugin.Log.LogInfo("Коллайдеры рельефа: живут " + _linger + " с вместо 1 с (фикс фризов физики).");
            }
            catch (Exception e) { Plugin.Log.LogError("Фикс коллайдеров рельефа не применён: " + e); }
        }

        /// <summary>Замеры для лагометра (если подробные замеры включены).</summary>
        public static void Profile(Harmony h)
        {
            ProfilerPatches.TimeMethod(h, AccessTools.Method(typeof(ColliderUpdater), "RefreshCollider"), "[Рельеф] постройка коллайдера чанка", "Script");
            ProfilerPatches.TimeMethod(h, AccessTools.Method(typeof(ColliderSectorUpdater), "RefreshChildren"), "[Рельеф] создание чанков сектора", "Script");
            ProfilerPatches.TimeMethod(h, AccessTools.Method(typeof(ColliderSectorUpdater), "Clear"), "[Рельеф] удаление чанков сектора", "Script");
        }

        // _activeUntil = 1f  →  _activeUntil = _linger
        private static IEnumerable<CodeInstruction> LingerTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int n = 0;
            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldc_R4 && (float)ins.operand == 1f) { ins.operand = _linger; n++; }
                yield return ins;
            }
            if (n != 1) Plugin.Log.LogWarning("Коллайдеры рельефа: ожидалась 1 константа 1.0, найдено " + n);
        }
    }
}
