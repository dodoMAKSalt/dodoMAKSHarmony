using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Network;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Вечное «Ожидаем других игроков» у хоста. Конец загрузки хоста (GameLoader) ждёт, пока у КАЖДОГО объекта User
    /// флаг LevelReady станет true; его ставит команда CmdSetLevelReady, которую шлёт клиент (и сам хост — локально).
    /// В логах (хост + друг, два запуска подряд): при смене сцены «SpawnWithClientAuthority NetworkConnection is not ready!»,
    /// «Could not produce class with ID 92», NRE в NetworkLobbyPlayer.OnSceneLoaded — у одного из User команда не доходит,
    /// хотя соединение готово, фракция готова и рельеф загружен (строки «connection ready True … level ready False»).
    ///
    /// Обход: если хост уже загрузился (HostLoaded), а у User с готовым соединением, фракцией и рельефом LevelReady так и
    /// нет дольше N секунд, хост выставляет его сам (на сервере это обычное поле). Клиент, который прислал бы команду, всё равно ждёт
    /// HostReady, так что игра стартует для всех. Пишется в лог; выключается [Network] LobbyRescue = false.
    /// </summary>
    internal static class LobbyRescue
    {
        private static bool _enabled;
        private static float _graceSeconds = 15f;
        private static float _sinceStuck = -1f;
        private static float _next;
        private static int _forced;

        public static void Init(bool enabled, float graceSeconds)
        {
            _enabled = enabled;
            _graceSeconds = Mathf.Max(5f, graceSeconds);
            if (enabled) Plugin.Log.LogInfo(string.Format("Страховка загрузки сетевой игры: через {0:0} с ожидания хост сам отметит готовность игроков с загруженным рельефом.", _graceSeconds));
        }

        public static void Tick()
        {
            if (!_enabled) return;
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 1f;
            try
            {
                var gp = GameParam.instance;
                var net = UNetManager.instance;
                if (gp == null || net == null || !NetworkServer.active || gp.HostReady || !gp.HostLoaded || net.UserList == null)
                {
                    _sinceStuck = -1f;
                    return;
                }
                bool waiting = false;
                foreach (var u in net.UserList)
                    if (u != null && !u.LevelReady) { waiting = true; break; }
                if (!waiting) { _sinceStuck = -1f; return; }
                if (_sinceStuck < 0f) { _sinceStuck = now; return; }
                if (now - _sinceStuck < _graceSeconds) return;

                var sb = new System.Text.StringBuilder();
                foreach (var u in net.UserList)
                {
                    if (u == null || u.LevelReady) continue;
                    var conn = u.connectionToClient;
                    bool local = conn == null; // собственный User хоста
                    bool connReady = local || conn.isReady;
                    if (connReady && u.TerrainLoaded && u.FactionReady)
                    {
                        u.LevelReady = true;
                        _forced++;
                        sb.Append(u.name).Append(local ? " (хост)" : "").Append("; ");
                    }
                }
                if (sb.Length > 0)
                {
                    Plugin.Log.LogWarning("Страховка загрузки: LevelReady не пришёл " + (int)(now - _sinceStuck) + " с — выставлен хостом: " + sb);
                    _sinceStuck = now; // дать игре время закончить загрузку
                }
            }
            catch (Exception e) { _enabled = false; Plugin.Log.LogWarning("Страховка загрузки выключена: " + e.Message); }
        }
    }
}
