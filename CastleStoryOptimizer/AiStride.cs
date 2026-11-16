using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// «Думают по очереди»: при большой армии у одной фракции и тяжёлом кадре половина её бриктронов пропускает шаг
    /// дерева поведения (Behavior.BrixFixedUpdate), а другая половина думает; на следующем шаге — наоборот.
    /// Стоимость ИИ падает вдвое (по замеру со 100 бриктронами Behavior.BrixFixedUpdate + Labor ≈ 45 из 50 мс кадра).
    ///
    /// Это не «общее решение для пары»: общие решения дали бы толпы на одной цели и поломки, если один из пары погиб.
    /// Каждый юнит по-прежнему думает сам, просто реже — раз в два шага физики (≈ 0,04 с). Движение, анимации и
    /// ожидания по времени не затронуты; ожидания «в тиках» у затронутых юнитов растягиваются вдвое.
    /// Включается только под нагрузкой (кадр дольше порога) и выключается, когда кадр снова лёгкий.
    /// </summary>
    internal static class AiStride
    {
        private sealed class Info { public Faction Faction; public int Seq; }

        private static readonly Dictionary<Behavior, Info> Cache = new Dictionary<Behavior, Info>();
        private static readonly HashSet<Faction> Heavy = new HashSet<Faction>();
        private static int _seq;
        private static float _lastFixed = -1f;
        private static int _step;

        private static bool _on;
        private static float _ema = 0.01f;
        private static float _next;
        private static int _minUnits = 30;
        private static float _onMs = 22f, _offMs = 15f;
        private static long _skipped, _ran;

        public static void Apply(Harmony h, int minUnits, float onMs)
        {
            _minUnits = Mathf.Max(2, minUnits);
            _onMs = Mathf.Max(8f, onMs);
            _offMs = _onMs * 0.7f;
            try
            {
                var m = AccessTools.Method(typeof(Behavior), "BrixFixedUpdate");
                h.Patch(m, prefix: new HarmonyMethod(typeof(AiStride), nameof(Prefix)) { priority = Priority.First });
                Plugin.Log.LogInfo(string.Format("ИИ по очереди: при кадре дольше {0:0} мс и ≥ {1} юнитах у фракции половина бриктронов думает на каждом шаге.", _onMs, _minUnits));
            }
            catch (Exception e) { Plugin.Log.LogWarning("ИИ по очереди не включён: " + e.Message); }
        }

        public static void Reset()
        {
            Cache.Clear();
            Heavy.Clear();
            _seq = 0;
            _on = false;
        }

        public static void Tick()
        {
            _ema = Mathf.Lerp(_ema, Time.unscaledDeltaTime, 0.05f);
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 0.5f;
            try
            {
                Heavy.Clear();
                var list = Faction.GetAllFactions();
                if (list != null)
                    foreach (var f in list)
                        if (f != null && f.UnitCount >= _minUnits) Heavy.Add(f);
                float ms = _ema * 1000f;
                bool want = Heavy.Count > 0 && (_on ? ms > _offMs : ms > _onMs);
                if (want != _on)
                {
                    _on = want;
                    Plugin.Log.LogInfo(string.Format("ИИ по очереди {0} (кадр {1:0.0} мс, фракций с большой армией: {2}).", _on ? "ВКЛ" : "выкл", ms, Heavy.Count));
                }
                if (Cache.Count > 4000) Cache.Clear();
            }
            catch (Exception e) { _on = false; Plugin.Log.LogWarning("ИИ по очереди: " + e.Message); }
        }

        public static string Summary()
        {
            return _skipped + _ran == 0 ? "" : string.Format("ИИ по очереди: {0}, пропущено шагов {1} из {2}", _on ? "включён" : "выключен", _skipped, _skipped + _ran);
        }

        private static bool Prefix(Behavior __instance)
        {
            if (!_on) return true;
            try
            {
                var labor = __instance as Labor;
                if (labor == null) return true;
                Info info;
                if (!Cache.TryGetValue(__instance, out info))
                {
                    var aff = __instance.GetComponent<AffiliationComponent>();
                    info = new Info { Faction = aff != null ? aff.faction : null, Seq = _seq++ };
                    Cache[__instance] = info;
                }
                if (info.Faction == null || !Heavy.Contains(info.Faction)) return true;
                float ft = Time.fixedTime;
                if (ft != _lastFixed) { _lastFixed = ft; _step++; }
                if (((info.Seq ^ _step) & 1) != 0) { _skipped++; return false; }
                _ran++;
            }
            catch { }
            return true;
        }
    }
}
