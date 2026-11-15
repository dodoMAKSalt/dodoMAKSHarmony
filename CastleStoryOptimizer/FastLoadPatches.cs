using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Brix.Engine;
using Brix.Engine.Blocks;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Ускорение загрузки катки — убираем искусственные ожидания в корутинах GameLoader и др.
    ///
    ///  1. Порции по 10 000 (гравитация рельефа, деревья, узлы путей) → ×ChunkMultiplier.
    ///     После каждой порции игра ждёт кадр — меньше порций = меньше пустых кадров.
    ///  2. Циклы «подождать 1 секунду и проверить снова» → проверка каждые 0.05 с.
    ///  3. Полоса прогресса «догоняет» максимум по 100 шагов за кадр → без ограничения.
    ///  4. Блоки построек: одиночка — ×ChunkMultiplier, мультиплеер — по заполненности очереди UNet.
    /// </summary>
    internal static class FastLoadPatches
    {
        private const int OriginalChunk = 10000;
        private const int OriginalBlocksPerYield = 350;

        private static int _chunk;
        private static int _blocksPerYield;
        private static readonly Dictionary<MethodBase, KeyValuePair<long, int>> IntReplacements = new Dictionary<MethodBase, KeyValuePair<long, int>>();

        public static void Apply(Harmony h, int multiplier, bool fastWaits)
        {
            multiplier = Mathf.Clamp(multiplier, 1, 100);
            _chunk = OriginalChunk * multiplier;
            _blocksPerYield = OriginalBlocksPerYield * multiplier;
            int ok = 0;

            if (multiplier > 1)
            {
                ok += ReplaceInt(h, "Brix.Engine.GameLoader", "InitTerrainGravity", OriginalChunk, _chunk);
                ok += ReplaceInt(h, "Brix.Engine.GameLoader", "LoadTrees", OriginalChunk, _chunk);
                ok += ReplaceInt(h, "Brix.Pathfinding.Test.TestPathfinding", "ReadAllPathNodes", OriginalChunk, _chunk);
                ok += ReplaceInt(h, "Brix.Pathfinding.Test.TestPathfinding", "InitPathfinding", OriginalChunk, _chunk);

                var lbd = AccessTools.Method(typeof(BlockEngine), "LoadBlockData");
                _registerBlock = AccessTools.Method(typeof(BlockEngine), "RegisterBlockDataStruct");
                if (lbd != null && _registerBlock != null)
                {
                    h.Patch(lbd, prefix: new HarmonyMethod(typeof(FastLoadPatches), nameof(LoadBlockDataPrefix)));
                    ok++;
                }
                else Plugin.Log.LogWarning("BlockEngine.LoadBlockData/RegisterBlockDataStruct не найдены — блоки грузятся как в оригинале.");
            }

            if (fastWaits)
            {
                ok += ReplaceInt(h, "Brix.Engine.GameLoader", "LoadingStepsCheckpoint", 100, int.MaxValue / 2);
                ok += ShortenWaits(h, "Brix.Engine.GameLoader", "BuildStep");
                ok += ShortenWaits(h, "Brix.Engine.GameLoader", "StartStep");
            }

            Plugin.Log.LogInfo(string.Format("Быстрая загрузка: патчей {0}, порции ×{1}, короткие ожидания: {2}", ok, multiplier, fastWaits));
        }

        // --- 1, 3: замена целочисленной константы внутри корутины ---
        private static int ReplaceInt(Harmony h, string type, string method, long from, int to)
        {
            var moveNext = IteratorMoveNext(type, method);
            if (moveNext == null) return 0;
            IntReplacements[moveNext] = new KeyValuePair<long, int>(from, to);
            try
            {
                h.Patch(moveNext, transpiler: new HarmonyMethod(typeof(FastLoadPatches), nameof(IntTranspiler)));
                return 1;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Не удалось пропатчить " + type + "." + method + ": " + e.Message); return 0; }
        }

        private static IEnumerable<CodeInstruction> IntTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var r = IntReplacements[original];
            int n = 0;
            foreach (var ins in instructions)
            {
                if (ins.LoadsConstant(r.Key))
                {
                    n++;
                    yield return new CodeInstruction(OpCodes.Ldc_I4, r.Value).MoveLabelsFrom(ins).MoveBlocksFrom(ins);
                }
                else yield return ins;
            }
            if (n == 0) Plugin.Log.LogWarning("Константа " + r.Key + " не найдена в " + original.DeclaringType);
        }

        // --- 2: WaitForSeconds(1f) → WaitForSeconds(0.05f) ---
        private static int ShortenWaits(Harmony h, string type, string method)
        {
            var moveNext = IteratorMoveNext(type, method);
            if (moveNext == null) return 0;
            try
            {
                h.Patch(moveNext, transpiler: new HarmonyMethod(typeof(FastLoadPatches), nameof(WaitTranspiler)));
                return 1;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Не удалось пропатчить " + type + "." + method + ": " + e.Message); return 0; }
        }

        private static IEnumerable<CodeInstruction> WaitTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var ctor = AccessTools.Constructor(typeof(WaitForSeconds), new[] { typeof(float) });
            var list = instructions.ToList();
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && (float)list[i].operand == 1f &&
                    list[i + 1].opcode == OpCodes.Newobj && Equals(list[i + 1].operand, ctor))
                    list[i].operand = 0.05f;
            }
            return list;
        }

        // --- 4: блоки построек ---
        // Каждый блок на хосте = отдельный NetworkServer.Spawn (сообщение каждому клиенту).
        // Оригинал вслепую ставит 350 блоков на шаг физики. Мы:
        //  * в одиночке — порции ×ChunkMultiplier;
        //  * в мультиплеере — смотрим на очередь отправки UNet и шлём столько, сколько тянет сеть.
        // Ставить мод клиенту для этого не обязательно (решает хост), но и не мешает.
        private static MethodInfo _registerBlock;

        private const int QueueLimit = 2048;     // UNetManager: MaxSentMessageQueueSize = 2048
        private const int MpMaxPerStep = 2000;   // потолок за шаг даже при пустой очереди

        private static bool LoadBlockDataPrefix(BlockEngine __instance, BlockDataStruct[] placedBlockData, ref System.Collections.IEnumerator __result)
        {
            if (!LoadingPatches.IsLoading || placedBlockData == null) return true; // вне загрузки — оригинал
            __result = LoadBlocks(__instance, placedBlockData);
            return false;
        }

        private static System.Collections.IEnumerator LoadBlocks(BlockEngine engine, BlockDataStruct[] blocks)
        {
            bool mp = Brix.Engine.Neo.IsMultiplayer && UnityEngine.Networking.NetworkServer.active;
            var args = new object[1];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int i = 0, steps = 0, stalls = 0;
            while (i < blocks.Length)
            {
                int budget = mp ? NetworkBudget() : _blocksPerYield;
                if (budget == 0) stalls++;
                for (int n = 0; n < budget && i < blocks.Length; n++, i++)
                {
                    args[0] = blocks[i];
                    _registerBlock.Invoke(engine, args);
                }
                steps++;
                yield return new WaitForFixedUpdate();
            }
            yield return null;
            Plugin.Log.LogInfo(string.Format("Блоки построек: {0} шт за {1:0.0} с, шагов {2}{3}",
                blocks.Length, sw.Elapsed.TotalSeconds, steps, mp ? ", мультиплеер, ожиданий сети: " + stalls : ""));
        }

        // ChannelBuffer — internal класс UNet, читаем его статическое поле через reflection.
        private static readonly FieldInfo PendingPackets =
            AccessTools.Field(AccessTools.TypeByName("UnityEngine.Networking.ChannelBuffer"), "pendingPacketCount");

        private static int NetworkBudget()
        {
            try
            {
                // Пакеты, которые UNet уже не смог отдать транспорту и держит в своём буфере (лимит 512 → разрыв).
                if (PendingPackets != null && (int)PendingPackets.GetValue(null) > 0) return 0;
                byte err;
                int q = UnityEngine.Networking.NetworkTransport.GetOutgoingMessageQueueSize(UnityEngine.Networking.NetworkServer.serverHostId, out err);
                if (err != 0) return OriginalBlocksPerYield;
                if (q < QueueLimit / 32) return MpMaxPerStep;  // < 64 пакетов — сеть свободна
                if (q < QueueLimit / 8) return 600;            // < 256
                if (q < QueueLimit / 4) return 150;            // < 512
                return 0;                                       // очередь забита — ждём
            }
            catch
            {
                return OriginalBlocksPerYield; // API недоступно — темп как в оригинале
            }
        }

        private static MethodInfo IteratorMoveNext(string type, string method)
        {
            var t = AccessTools.TypeByName(type);
            var m = t == null ? null : AccessTools.Method(t, method);
            var mn = m == null ? null : AccessTools.EnumeratorMoveNext(m);
            if (mn == null) Plugin.Log.LogWarning("Не найдена корутина " + type + "." + method);
            return mn;
        }
    }
}
