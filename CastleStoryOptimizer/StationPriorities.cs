using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Brix.Assets;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.UI.GameSelector;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Приоритет для каждой мастерской (печь, верстак, кузница, ткацкий станок, колода и т.д.).
    ///
    /// В игре у мастерской есть CraftingStation.PriorityBias, который входит в формулу выбора работы
    /// (ProjectPriority.manualBias, вес 1), но он захардкожен = High (8) и не меняется.
    /// Мы подставляем свой уровень для каждой мастерской:
    ///   Выключена → мастерская не предлагает работу (HasWorkFor = false)
    ///   Низкий 0 / Средний 4 / Обычный 8 (как в игре) / Высокий 16 / Срочно 40
    ///
    /// Управление: выделить мастерскую(и) → PageUp / PageDown.
    /// Хранение: файл dodo_station_priorities.txt рядом с сохранением.
    /// Мультиплеер: ИИ считается на хосте — клиент шлёт выбор хосту, хост рассылает всем.
    /// </summary>
    internal static class StationPriorities
    {
        public const int DefaultLevel = 3;
        private static readonly int[] Bias = { -1000, 0, 4, 8, 16, 40 };
        private static readonly string[] Names = { "Выключена", "Низкий", "Средний", "Обычный", "Высокий", "СРОЧНО" };
        private static readonly Color[] Colors =
        {
            new Color(0.6f, 0.6f, 0.6f), new Color(0.55f, 0.75f, 1f), new Color(0.75f, 0.9f, 1f),
            Color.white, new Color(1f, 0.77f, 0.04f), new Color(1f, 0.3f, 0.2f)
        };

        private const string SaveFile = "dodo_station_priorities.txt";
        private const short MsgSet = 31337;       // клиент → хост и хост → все: (netId, level)

        private static readonly Dictionary<CraftingStation, int> Levels = new Dictionary<CraftingStation, int>();
        private static KeyCode _up, _down;
        private static bool _showLabels;
        private static List<GameObject> Selected = new List<GameObject>();
        private static readonly List<CraftingStation> SelectedStations = new List<CraftingStation>();
        private static GUIStyle _labelStyle, _hintStyle;
        private static float _nextSelectionPoll;

        public static void Apply(Harmony h, KeyCode up, KeyCode down, bool showLabels)
        {
            _up = up; _down = down; _showLabels = showLabels;
            try
            {
                var t = typeof(CraftingStation);
                h.Patch(AccessTools.PropertyGetter(t, nameof(CraftingStation.PriorityBias)),
                    postfix: new HarmonyMethod(typeof(StationPriorities), nameof(BiasPostfix)));
                h.Patch(AccessTools.Method(t, nameof(CraftingStation.HasWorkFor)),
                    postfix: new HarmonyMethod(typeof(StationPriorities), nameof(HasWorkPostfix)));
                h.Patch(AccessTools.Method(typeof(Asset_Map), "Disk_WriteGameObjects"),
                    postfix: new HarmonyMethod(typeof(StationPriorities), nameof(SavePostfix)));
                Plugin.Log.LogInfo("Приоритеты мастерских включены (" + up + " / " + down + ").");
            }
            catch (Exception e) { Plugin.Log.LogError("Приоритеты мастерских не включены: " + e); }
        }

        // ---------- ядро ----------
        public static int GetLevel(CraftingStation s)
        {
            int l;
            return s != null && Levels.TryGetValue(s, out l) ? l : DefaultLevel;
        }

        private static void SetLevelLocal(CraftingStation s, int level)
        {
            level = Mathf.Clamp(level, 0, Bias.Length - 1);
            if (level == DefaultLevel) Levels.Remove(s); else Levels[s] = level;
        }

        private static void BiasPostfix(CraftingStation __instance, ref ProjectPriority.Bias __result)
        {
            int l;
            if (Levels.TryGetValue(__instance, out l)) __result = (ProjectPriority.Bias)Bias[l];
        }

        private static void HasWorkPostfix(CraftingStation __instance, ref bool __result)
        {
            if (__result && GetLevel(__instance) == 0) __result = false;
        }

        // ---------- ввод и отрисовка (вызывается из Plugin) ----------
        public static void Tick()
        {
            if (Brix.Engine.Architecte.instance == null) { if (Levels.Count > 0) Levels.Clear(); return; }

            if (Time.unscaledTime >= _nextSelectionPoll)
            {
                _nextSelectionPoll = Time.unscaledTime + 0.1f;
                Selected.Clear();
                SelectedStations.Clear();
                if (UIGameSelector.instance != null)
                {
                    try { UIGameSelector.GetSelectedEverything(ref Selected); } catch { }
                    foreach (var go in Selected)
                    {
                        var st = go == null ? null : go.GetComponent<CraftingStation>();
                        if (st != null) SelectedStations.Add(st);
                    }
                }
            }
            if (SelectedStations.Count == 0) return;

            int delta = Input.GetKeyDown(_up) ? 1 : Input.GetKeyDown(_down) ? -1 : 0;
            if (delta == 0) return;
            foreach (var st in SelectedStations)
                RequestSet(st, GetLevel(st) + delta);
        }

        public static void DrawGUI()
        {
            if (Brix.Engine.Architecte.instance == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 13 };
                _hintStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 16 };
            }

            if (_showLabels)
            {
                foreach (var kv in Levels) DrawLabel(cam, kv.Key, kv.Value);
                foreach (var st in SelectedStations) if (!Levels.ContainsKey(st)) DrawLabel(cam, st, DefaultLevel);
            }
            // Верхняя подсказка про PageUp/PageDown убрана: приоритет выбирается в круговом меню,
            // а уровень и так виден в подписи над мастерской.
        }

        private static void DrawLabel(Camera cam, CraftingStation st, int level)
        {
            if (st == null) return;
            var p = cam.WorldToScreenPoint(st.transform.position + Vector3.up * 2.2f);
            if (p.z <= 0 || p.z > 120f) return;
            var r = new Rect(p.x - 70, Screen.height - p.y - 12, 140, 24);
            Shadowed(r, Names[level], Colors[level], _labelStyle);
        }

        private static void Shadowed(Rect r, string text, Color c, GUIStyle style)
        {
            style.normal.textColor = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
            style.normal.textColor = c;
            GUI.Label(r, text, style);
        }

        // ---------- сеть ----------
        private class SetMsg : MessageBase
        {
            public NetworkInstanceId NetId;
            public int Level;
            public override void Serialize(NetworkWriter w) { w.Write(NetId); w.Write(Level); }
            public override void Deserialize(NetworkReader r) { NetId = r.ReadNetworkId(); Level = r.ReadInt32(); }
        }

        public static string NameOf(int level) => Names[Mathf.Clamp(level, 0, Names.Length - 1)];

        internal static void RequestSet(CraftingStation st, int level)
        {
            level = Mathf.Clamp(level, 0, Bias.Length - 1);
            bool mp = Brix.Engine.Neo.IsMultiplayer;
            var id = st.GetComponent<NetworkIdentity>();

            if (!mp || NetworkServer.active || id == null)
            {
                SetLevelLocal(st, level);
                if (mp && NetworkServer.active && id != null)
                    NetworkServer.SendToAll(MsgSet, new SetMsg { NetId = id.netId, Level = level });
                return;
            }
            // Клиент: показываем сразу, решение — на хосте
            SetLevelLocal(st, level);
            var client = NetworkManager.singleton != null ? NetworkManager.singleton.client : null;
            if (client != null && client.isConnected)
                client.Send(MsgSet, new SetMsg { NetId = id.netId, Level = level });
        }

        /// <summary>Регистрирует обработчики сообщений; вызывается после загрузки катки.</summary>
        public static void RegisterNetwork()
        {
            try
            {
                if (NetworkServer.active)
                    NetworkServer.RegisterHandler(MsgSet, OnServerMsg);
                var client = NetworkManager.singleton != null ? NetworkManager.singleton.client : null;
                if (client != null)
                    client.RegisterHandler(MsgSet, OnClientMsg);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Сеть приоритетов: " + e.Message); }
        }

        private static CraftingStation Find(NetworkInstanceId id)
        {
            var go = NetworkServer.active ? NetworkServer.FindLocalObject(id) : ClientScene.FindLocalObject(id);
            return go == null ? null : go.GetComponent<CraftingStation>();
        }

        private static void OnServerMsg(NetworkMessage m)
        {
            var msg = m.ReadMessage<SetMsg>();
            var st = Find(msg.NetId);
            if (st == null) return;
            SetLevelLocal(st, msg.Level);
            NetworkServer.SendToAll(MsgSet, msg);
        }

        private static void OnClientMsg(NetworkMessage m)
        {
            if (NetworkServer.active) return; // хост уже применил у себя
            var msg = m.ReadMessage<SetMsg>();
            var st = Find(msg.NetId);
            if (st != null) SetLevelLocal(st, msg.Level);
        }

        // ---------- сохранение ----------
        private static string Key(CraftingStation s)
        {
            var p = s.transform.position;
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1:0.0}|{2:0.0}|{3:0.0}",
                s.GetType().Name, p.x, p.y, p.z);
        }

        private static void SavePostfix(Asset_Map __instance)
        {
            try
            {
                var file = System.IO.Path.Combine(__instance.path, SaveFile);
                if (Levels.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
                var sb = new StringBuilder("# dodoMAKSharmony: приоритеты мастерских (тип|x|y|z=уровень 0..5)\n");
                foreach (var kv in Levels)
                    if (kv.Key != null) sb.Append(Key(kv.Key)).Append('=').Append(kv.Value).Append('\n');
                File.WriteAllText(file, sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Не удалось сохранить приоритеты мастерских: " + e.Message); }
        }

        /// <summary>После загрузки катки: восстановить приоритеты из сохранения (только хост/одиночка).</summary>
        public static void OnLevelLoaded()
        {
            Levels.Clear();
            RegisterNetwork();
            if (Brix.Engine.Neo.IsMultiplayer && !NetworkServer.active) return;
            try
            {
                var map = GameParam.Map;
                if (map == null) return;
                var file = System.IO.Path.Combine(map.path, SaveFile);
                if (!File.Exists(file)) return;
                var wanted = new Dictionary<string, int>();
                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.StartsWith("#")) continue;
                    int eq = line.LastIndexOf('=');
                    int lvl;
                    if (eq > 0 && int.TryParse(line.Substring(eq + 1), out lvl)) wanted[line.Substring(0, eq)] = lvl;
                }
                int applied = 0;
                foreach (var st in UnityEngine.Object.FindObjectsOfType<CraftingStation>())
                {
                    int lvl;
                    if (wanted.TryGetValue(Key(st), out lvl)) { SetLevelLocal(st, lvl); applied++; }
                }
                Plugin.Log.LogInfo("Приоритеты мастерских восстановлены: " + applied + " из " + wanted.Count);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Не удалось загрузить приоритеты мастерских: " + e.Message); }
        }
    }
}
