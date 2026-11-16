using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Замер задержки сетевой игры: каждые 2 с хост шлёт каждому клиенту «пинг» обычным сетевым сообщением, клиент сразу
    /// отвечает «понгом»; хост считает круговую задержку по каждому клиенту. Это тот же путь, что у команд игроков
    /// (Steam P2P / UNet), поэтому цифра показывает, сколько команда реально идёт по сети. Если пинг низкий, а команды
    /// всё равно запаздывают — задержка не в канале, а в игре. Итоги — в лог каждые 20 с и в отчёт замерщика.
    /// </summary>
    internal static class NetPing
    {
        private const short MsgPing = 31341, MsgPong = 31342;

        private sealed class Stat
        {
            public readonly float[] Ring = new float[15];
            public int Count, Pos;
            public float Last, Max;
            public void Add(float ms)
            {
                Last = ms;
                Ring[Pos] = ms; Pos = (Pos + 1) % Ring.Length; if (Count < Ring.Length) Count++;
                if (ms > Max) Max = ms;
            }
            public float Avg() { float s = 0; for (int i = 0; i < Count; i++) s += Ring[i]; return Count == 0 ? 0 : s / Count; }
            public float RecentMax() { float m = 0; for (int i = 0; i < Count; i++) if (Ring[i] > m) m = Ring[i]; return m; }
        }

        private class PingMsg : MessageBase
        {
            public float Sent;
            public override void Serialize(NetworkWriter w) { w.Write(Sent); }
            public override void Deserialize(NetworkReader r) { Sent = r.ReadSingle(); }
        }

        private static readonly Dictionary<int, Stat> Stats = new Dictionary<int, Stat>();
        private static float _nextPing, _nextLog;
        private static bool _enabled = true;
        private static string _last = "";

        public static void Init(bool enabled) { _enabled = enabled; }

        /// <summary>Вызывается в конце загрузки катки (обработчики сообщений привязаны к текущему соединению).</summary>
        public static void Register()
        {
            if (!_enabled) return;
            Stats.Clear();
            try
            {
                if (NetworkServer.active)
                    NetworkServer.RegisterHandler(MsgPong, delegate (NetworkMessage m)
                    {
                        var msg = m.ReadMessage<PingMsg>();
                        Stat st;
                        if (!Stats.TryGetValue(m.conn.connectionId, out st)) Stats[m.conn.connectionId] = st = new Stat();
                        st.Add((Time.realtimeSinceStartup - msg.Sent) * 1000f);
                    });
                var client = NetworkManager.singleton != null ? NetworkManager.singleton.client : null;
                if (client != null && !NetworkServer.active)
                    client.RegisterHandler(MsgPing, delegate (NetworkMessage m)
                    {
                        var msg = m.ReadMessage<PingMsg>();
                        var c = NetworkManager.singleton != null ? NetworkManager.singleton.client : null;
                        if (c != null && c.isConnected) c.Send(MsgPong, msg);
                    });
            }
            catch (Exception e) { Plugin.Log.LogWarning("Пинг: " + e.Message); }
        }

        public static void Tick()
        {
            if (!_enabled || !NetworkServer.active) return;
            float now = Time.realtimeSinceStartup;
            if (now >= _nextPing)
            {
                _nextPing = now + 2f;
                try
                {
                    if (NetworkServer.connections.Count > 1 || (NetworkServer.connections.Count == 1 && Brix.Engine.Neo.IsMultiplayer))
                        NetworkServer.SendToAll(MsgPing, new PingMsg { Sent = now });
                }
                catch (Exception e) { _enabled = false; Plugin.Log.LogWarning("Пинг выключен: " + e.Message); }
            }
            if (now >= _nextLog && Stats.Count > 0)
            {
                _nextLog = now + 20f;
                _last = Summary();
                if (_last.Length > 0) Plugin.Log.LogInfo(_last);
            }
        }

        public static string Summary()
        {
            if (Stats.Count == 0) return "";
            var sb = new StringBuilder("Сеть (круговая задержка хост↔клиент):");
            foreach (var kv in Stats)
            {
                var s = kv.Value;
                sb.AppendFormat(" клиент {0}: сейчас {1:0} мс, средний {2:0}, макс {3:0} (последние {4}), за всё время макс {5:0} мс;", kv.Key, s.Last, s.Avg(), s.RecentMax(), s.Count, s.Max);
            }
            return sb.ToString();
        }
    }
}
