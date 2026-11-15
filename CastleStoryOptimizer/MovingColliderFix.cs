using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Регулярные фризы ~50 мс в фазе физики (раз в ~1.4–2 с) при том, что скрипты коллайдеры не меняют.
    /// Типичная причина в PhysX 3.3 / Unity 5: СТАТИЧНЫЙ коллайдер (без Rigidbody) кто-то двигает
    /// (аниматор, скрипт) — PhysX копит «грязные» статики и периодически перестраивает дерево всех ~1000.
    /// Лечение по документации Unity: движущемуся коллайдеру — кинематический Rigidbody.
    ///
    /// Поиск — только ЧТЕНИЕМ (позиция/поворот/масштаб, сравнение через 0.5 с). transform.hasChanged
    /// не трогаем: игра использует его как флаг кэша позиции (VoxelTransform).
    /// Фикс: тем, кто двигался в 2 проверках подряд, добавляем Rigidbody { isKinematic, без гравитации }.
    /// Клавиша «-» — включить/выключить фикс на лету (для сравнения).
    /// </summary>
    internal sealed class MovingColliderFix
    {
        private struct Snap { public Collider C; public Vector3 P; public Quaternion R; public Vector3 S; }

        private readonly List<Snap> _snaps = new List<Snap>();
        private readonly Dictionary<int, int> _moveCount = new Dictionary<int, int>(); // id коллайдера → сколько проверок подряд двигался
        private readonly List<Rigidbody> _added = new List<Rigidbody>();
        private readonly HashSet<int> _fixedIds = new HashSet<int>();
        private readonly Dictionary<string, int> _lastMovers = new Dictionary<string, int>();
        private float _nextScan, _compareAt;
        private int _scans, _lastMoverCount;
        public bool Enabled;

        public MovingColliderFix(bool enabled) { Enabled = enabled; }

        public static string LastReport = string.Empty;

        public void Tick()
        {
            if (!Enabled || Brix.Engine.Architecte.instance == null || LoadingPatches.IsLoading)
            {
                if (_snaps.Count > 0 || _added.Count > 0) Reset();
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (_compareAt > 0 && now >= _compareAt) { Compare(); _compareAt = 0; }
            if (now >= _nextScan) { TakeSnapshot(); _nextScan = now + 10f; _compareAt = now + 0.5f; }
        }

        private void Reset()
        {
            _snaps.Clear(); _moveCount.Clear(); _added.Clear(); _fixedIds.Clear(); _lastMovers.Clear();
            _scans = 0; _compareAt = 0;
        }

        private void TakeSnapshot()
        {
            _snaps.Clear();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (!c.enabled || c.attachedRigidbody != null) continue;
                var t = c.transform;
                _snaps.Add(new Snap { C = c, P = t.position, R = t.rotation, S = t.lossyScale });
            }
        }

        private void Compare()
        {
            _scans++;
            _lastMovers.Clear();
            var stillMoving = new HashSet<int>();
            int movers = 0;
            foreach (var s in _snaps)
            {
                var c = s.C;
                if (c == null || !c.enabled || c.attachedRigidbody != null) continue;
                var t = c.transform;
                bool moved = (t.position - s.P).sqrMagnitude > 1e-6f || Quaternion.Angle(t.rotation, s.R) > 0.01f
                             || (t.lossyScale - s.S).sqrMagnitude > 1e-6f;
                if (!moved) continue;
                movers++;
                int id = c.GetInstanceID();
                stillMoving.Add(id);
                int n;
                _moveCount.TryGetValue(id, out n);
                _moveCount[id] = n + 1;
                string key = Describe(c);
                int k;
                _lastMovers.TryGetValue(key, out k);
                _lastMovers[key] = k + 1;
                if (Enabled && n + 1 >= 2) AddKinematic(c);
            }
            // кто перестал двигаться — сбрасываем счётчик
            foreach (var id in _moveCount.Keys.ToList()) if (!stillMoving.Contains(id)) _moveCount.Remove(id);

            _lastMoverCount = movers;
            LastReport = string.Format("движущихся статичных коллайдеров {0} из {1}, исправлено (кинем. тело) {2}{3}{4}",
                movers, _snaps.Count, _added.Count(r => r != null), Enabled ? "" : " [фикс ВЫКЛ]",
                movers > 0 ? ": " + string.Join(", ", _lastMovers.OrderByDescending(kv => kv.Value).Take(8)
                    .Select(kv => kv.Key + "×" + kv.Value).ToArray()) : "");
            if (_scans <= 3 || _scans % 6 == 0) Plugin.Log.LogInfo("Физика: " + LastReport);
        }

        private void AddKinematic(Collider c)
        {
            var go = c.gameObject;
            if (!_fixedIds.Add(go.GetInstanceID())) return;
            if (go.GetComponent<Rigidbody>() != null || go.GetComponent<CharacterController>() != null) return;
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _added.Add(rb);
        }

        /// <summary>«-» в игре: убрать/вернуть добавленные тела.</summary>
        public void Toggle()
        {
            Enabled = !Enabled;
            if (!Enabled)
            {
                int n = 0;
                foreach (var rb in _added) if (rb != null) { UnityEngine.Object.Destroy(rb); n++; }
                _added.Clear();
                _fixedIds.Clear();
                Plugin.Log.LogInfo("Фикс движущихся коллайдеров ВЫКЛ (убрано тел: " + n + ")");
            }
            else { _nextScan = 0; Plugin.Log.LogInfo("Фикс движущихся коллайдеров ВКЛ (тела добавятся на следующих проверках)"); }
        }

        private static string Describe(Collider c)
        {
            string root = Clean(c.transform.root.name);
            string self = Clean(c.gameObject.name);
            return (root == self ? self : root + "/" + self) + "(" + c.GetType().Name.Replace("Collider", "") + ")";
        }

        private static string Clean(string n)
        {
            int i = n.IndexOf('(');
            return (i > 0 ? n.Substring(0, i) : n).Trim();
        }
    }
}
