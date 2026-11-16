using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Убирает из мира сундуки и «зверей»-стражей Castle Story Plus (Treasure). Когда Treasure выключен, их юниты
    /// остаются в сохранении обычными големами (корраптроны/бифтроны), а сундуки — просто сундуками.
    /// Стража узнаём по WorkerStats.Variant >= 10 (так их помечает CSP и пишет в сохранение), сундуки — по
    /// координатам из castlestoryplus_treasure.json в папке карты. Всё через рефлексию, файлы CSP не трогаем.
    /// Два прохода после загрузки уровня: на 20-й и 60-й секунде.
    /// </summary>
    internal static class TreasureCleanup
    {
        private static readonly float[] Passes = { 20f, 60f };
        private static float _last;
        private static int _next;
        private static bool _enabled;

        public static void Enable() { _enabled = true; }

        public static void Tick()
        {
            if (!_enabled) return;
            float t = Time.timeSinceLevelLoad;
            if (t < _last) _next = 0; // новая сцена
            _last = t;
            if (_next >= Passes.Length || t < Passes[_next]) return;
            _next++;
            try { Run(); }
            catch (Exception e) { Plugin.Log.LogWarning("Очистка сундуков/големов Treasure: " + e.Message); }
        }

        private static Type T(string name) { return AccessTools.TypeByName(name); }

        private static object Member(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var t = o as Type ?? o.GetType();
            object target = o is Type ? null : o;
            var f = t.GetField(name, F);
            if (f != null) return f.GetValue(target);
            var p = t.GetProperty(name, F);
            return p != null ? p.GetValue(target, null) : null;
        }

        private static void Run()
        {
            var labor = T("Labor");
            var stats = T("CastleStoryPlus.Workers.WorkerStats");
            if (labor == null || stats == null) return;
            var peek = AccessTools.Method(stats, "Peek");
            var variant = stats.GetField("Variant");
            if (peek == null || variant == null) return;

            var removed = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(labor))
            {
                var c = o as Component;
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                var state = Member(o, "state");
                if (state == null) continue;
                var s = peek.Invoke(null, new[] { state });
                if (s == null) continue;
                var v = (int)variant.GetValue(s);
                if (v < 10 || v > 13) continue;
                Remove(c.gameObject);
                removed++;
            }

            int chests = 0;
            var findChest = AccessTools.Method(T("CastleStoryPlus.Treasure.TreasureChests"), "FindChest");
            foreach (var pos in ChestPositions())
            {
                for (int i = 0; i < 3 && findChest != null; i++) // на одном месте мог оказаться дубль
                {
                    var go = findChest.Invoke(null, new object[] { pos }) as GameObject;
                    if (go == null) break;
                    Remove(go);
                    chests++;
                }
            }
            if (removed > 0 || chests > 0)
                Plugin.Log.LogInfo("Treasure убран: стражей-големов " + removed + ", сундуков " + chests + ".");
        }

        private static void Remove(GameObject go)
        {
            try
            {
                var ns = T("UnityEngine.Networking.NetworkServer");
                var destroy = ns != null ? ns.GetMethod("Destroy", new[] { typeof(GameObject) }) : null;
                if (destroy != null) { destroy.Invoke(null, new object[] { go }); return; }
            }
            catch { }
            UnityEngine.Object.Destroy(go);
        }

        private static List<Vector3> ChestPositions()
        {
            var list = new List<Vector3>();
            var gp = T("GameParam");
            var map = Member(gp, "Map");
            var path = Member(map, "path") as string;
            if (string.IsNullOrEmpty(path)) return list;
            var file = Path.Combine(path, "castlestoryplus_treasure.json");
            if (!File.Exists(file)) return list;
            var text = File.ReadAllText(file);
            var re = new Regex("\"x\"\\s*:\\s*(-?[0-9.eE+-]+)\\s*,\\s*\"y\"\\s*:\\s*(-?[0-9.eE+-]+)\\s*,\\s*\"z\"\\s*:\\s*(-?[0-9.eE+-]+)");
            foreach (Match m in re.Matches(text))
                list.Add(new Vector3(P(m.Groups[1].Value), P(m.Groups[2].Value), P(m.Groups[3].Value)));
            return list;
        }

        private static float P(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
    }
}
