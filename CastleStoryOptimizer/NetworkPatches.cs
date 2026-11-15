using System;
using System.Collections;
using System.Net;
using System.Threading;
using Brix.Network;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Фриз ~11 с при создании хоста / подключении.
    ///
    /// NATHelper.connectToNATFacilitator() вызывает RakNet Connect("facilitator.castlestory.net"),
    /// а RakNet резолвит DNS синхронно, на главном потоке. Домен давно мёртв — запрос висит ~11 с и падает.
    ///
    /// Фикс: резолвим DNS в фоне (заранее, при старте игры). Если адрес есть — отдаём RakNet готовый IP
    /// (без DNS на главном потоке). Если домена нет — пропускаем подключение к фасилитатору:
    /// он всё равно бы не подключился, игра откатится на прямое подключение, как и раньше.
    /// </summary>
    internal static class NetworkPatches
    {
        private const string Host = "facilitator.castlestory.net";
        private static volatile int _state; // 0 — не знаем, 1 — резолвится, 2 — есть IP, 3 — домена нет
        private static string _ip;
        private static DateTime _failedAt;

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(NATHelper), nameof(NATHelper.connectToNATFacilitator)),
                    prefix: new HarmonyMethod(typeof(NetworkPatches), nameof(ConnectPrefix)));
                StartResolve();
                Plugin.Log.LogInfo("Фикс фриза хоста (DNS фасилитатора в фоне) включён.");
            }
            catch (Exception e) { Plugin.Log.LogError("Фикс фриза хоста не применён: " + e); }
        }

        private static void StartResolve()
        {
            if (_state == 1) return;
            _state = 1;
            new Thread(() =>
            {
                try
                {
                    var addrs = Dns.GetHostAddresses(Host);
                    foreach (var a in addrs)
                        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { _ip = a.ToString(); break; }
                    _state = _ip != null ? 2 : 3;
                }
                catch { _state = 3; }
                if (_state == 3) _failedAt = DateTime.Now;
            }) { IsBackground = true, Name = "FacilitatorDNS" }.Start();
        }

        private static bool ConnectPrefix(NATHelper __instance)
        {
            IPAddress dummy;
            if (IPAddress.TryParse(__instance.facilitatorIP, out dummy)) return true; // уже IP — DNS не будет

            // Неудача старше 10 минут — пробуем ещё раз (вдруг интернет появился)
            if (_state == 3 && (DateTime.Now - _failedAt).TotalMinutes > 10) StartResolve();

            switch (_state)
            {
                case 2:
                    __instance.facilitatorIP = _ip;
                    return true;
                case 3:
                    Plugin.Log.LogInfo("NAT-фасилитатор " + Host + " недоступен — пропускаем (без фриза).");
                    return false;
                default:
                    __instance.StartCoroutine(WaitAndConnect(__instance));
                    return false;
            }
        }

        private static IEnumerator WaitAndConnect(NATHelper helper)
        {
            float until = Time.realtimeSinceStartup + 20f;
            while (_state < 2 && Time.realtimeSinceStartup < until) yield return null;
            if (helper == null) yield break;
            if (_state == 2)
            {
                helper.facilitatorIP = _ip;
                helper.connectToNATFacilitator(); // префикс увидит IP и пропустит оригинал
            }
            else Plugin.Log.LogInfo("NAT-фасилитатор " + Host + " недоступен — пропускаем (без фриза).");
        }
    }
}
