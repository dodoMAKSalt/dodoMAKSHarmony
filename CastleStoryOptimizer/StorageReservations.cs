using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Semantique;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Два бриктрона бегут сложить ресурсы в одно хранилище, места хватает одному — второй добегает,
    /// «тупит» пару секунд и идёт к другому.
    ///
    /// Игра выбирает «ближайшее хранилище, где есть место» (Knowledge.ClosestStorageWithRoomFor), не учитывая тех,
    /// кто уже несёт туда груз. Добавляем бронь: выбрал хранилище — его груз «занимает» место, пока не сложит
    /// (или не опустеет инвентарь / не пройдёт 60 с). Остальные при выборе видят место за вычетом чужих броней.
    /// Если из-за броней не подошло НИ ОДНО хранилище — повторяем выбор по-старому (чтобы груз не бросали на землю).
    /// </summary>
    internal static class StorageReservations
    {
        private sealed class Res { public Labor Labor; public Description Load; public float Time; }

        private static readonly Dictionary<Recepteur, List<Res>> ByStorage = new Dictionary<Recepteur, List<Res>>();
        private static readonly Dictionary<Labor, Recepteur> ByLabor = new Dictionary<Labor, Recepteur>();
        private const float Ttl = 60f;

        [ThreadStatic] private static Labor _asking;   // кто сейчас выбирает хранилище
        [ThreadStatic] private static bool _bypass;    // второй проход — без броней

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Knowledge), nameof(Knowledge.ClosestStorageWithRoomFor)),
                    prefix: new HarmonyMethod(typeof(StorageReservations), nameof(ClosestPrefix)),
                    postfix: new HarmonyMethod(typeof(StorageReservations), nameof(ClosestPostfix)));
                h.Patch(AccessTools.Method(typeof(Knowledge), "_ClosestStorageWithRoomFor_Test"),
                    postfix: new HarmonyMethod(typeof(StorageReservations), nameof(TestPostfix)));
                Plugin.Log.LogInfo("ИИ: бронь места в хранилищах (не бегут вдвоём в одно место).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("ИИ: бронь хранилищ не применена: " + e.Message); }
        }

        private static void ClosestPrefix(Labor source, out Labor __state)
        {
            __state = _asking;
            _asking = source;
        }

        private static void ClosestPostfix(Knowledge __instance, Labor source, IGameComponent itemToStore, Faction faction,
            ref IGameComponent __result, Labor __state)
        {
            _asking = __state;
            if (_bypass || source == null) return;
            try
            {
                if (__result == null && HasOtherReservations(source))
                {
                    // всё «занято бронями» — выбираем как раньше, без учёта броней
                    _bypass = true;
                    try { __result = __instance.ClosestStorageWithRoomFor(source, itemToStore, faction); }
                    finally { _bypass = false; }
                }
                Reserve(source, __result);
            }
            catch (Exception e) { Plugin.Log.LogDebug("Бронь хранилища: " + e.Message); }
        }

        /// <summary>Есть место «по-честному», но с учётом чужих броней — может уже не быть.</summary>
        private static void TestPostfix(IGameComponent gc, IGameComponent itemToStore, ref bool __result)
        {
            if (!__result || _bypass || _asking == null || gc == null || itemToStore == null) return;
            try
            {
                var r = gc.recepteur;
                List<Res> list;
                if (r == null || !ByStorage.TryGetValue(r, out list)) return;
                Description left = null;
                float now = Time.time;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var res = list[i];
                    if (!Valid(res, now)) { list.RemoveAt(i); if (res.Labor != null) ByLabor.Remove(res.Labor); continue; }
                    if (res.Labor == _asking) continue;
                    if (left == null) left = r.CurrentCapacity.Clone();
                    left.Subtract(res.Load);
                }
                if (left == null) return;
                var d = itemToStore.GameObject != null ? itemToStore.GameObject.GetComponent<IDescriptor>() : null;
                if (d != null && !left.PeutPrendre(d)) __result = false;
            }
            catch { /* при любой странности — как в игре */ }
        }

        private static bool Valid(Res res, float now)
        {
            if (res.Labor == null || now - res.Time > Ttl) return false;
            var inv = res.Labor.recepteur;
            return inv != null && !inv.IsEmpty();
        }

        private static bool HasOtherReservations(Labor me)
        {
            float now = Time.time;
            foreach (var kv in ByStorage)
                foreach (var res in kv.Value)
                    if (res.Labor != me && Valid(res, now)) return true;
            return false;
        }

        private static void Reserve(Labor labor, IGameComponent storage)
        {
            Recepteur old;
            if (ByLabor.TryGetValue(labor, out old))
            {
                List<Res> ol;
                if (ByStorage.TryGetValue(old, out ol)) ol.RemoveAll(x => x.Labor == labor);
                ByLabor.Remove(labor);
            }
            if (storage == null) return;
            var r = storage.recepteur;
            var inv = labor.recepteur;
            if (r == null || inv == null || inv.IsEmpty()) return;

            var load = new Description();
            foreach (var item in inv.StoredItems)
                if (item != null && item.Description != null) load = load + item.Description;

            List<Res> list;
            if (!ByStorage.TryGetValue(r, out list)) ByStorage[r] = list = new List<Res>();
            list.Add(new Res { Labor = labor, Load = load, Time = Time.time });
            ByLabor[labor] = r;
        }

        public static void Clear() { ByStorage.Clear(); ByLabor.Clear(); }
    }
}
