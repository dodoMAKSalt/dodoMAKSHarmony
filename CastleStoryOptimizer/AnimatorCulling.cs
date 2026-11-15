using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// В большом замке ~600 аниматоров (двери, мастерские, склады, кристаллы…), из них видно ~80.
    /// По умолчанию в Unity 5.6 аниматор работает в режиме AlwaysAnimate — анимирует и за кадром.
    ///
    /// Переводим «реквизит» в CullUpdateTransforms: за кадром state machine, события и логика
    /// продолжают идти, пропускается только запись трансформов/IK — самая дорогая часть.
    /// Не трогаем: персонажей (есть SkinnedMeshRenderer), интерфейс (Canvas — у него нет «видимости»),
    /// аниматоры без рендереров (могут управлять логикой) и тех, кому игра уже сама выставила режим.
    /// </summary>
    internal sealed class AnimatorCulling
    {
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly List<Animator> _list = new List<Animator>();
        public bool Enabled = true;

        /// <summary>«+» — сравнить FPS с оптимизацией и без, не перезапуская игру.</summary>
        public void Toggle()
        {
            Enabled = !Enabled;
            var mode = Enabled ? AnimatorCullingMode.CullUpdateTransforms : AnimatorCullingMode.AlwaysAnimate;
            int n = 0;
            foreach (var a in _list) if (a != null) { a.cullingMode = mode; n++; }
            Plugin.Log.LogInfo("Оптимизация аниматоров " + (Enabled ? "ВКЛ" : "ВЫКЛ") + " (" + n + " шт)");
        }
        private readonly Dictionary<string, int> _culledByName = new Dictionary<string, int>();
        private float _nextScan, _nextLog;
        private int _culled, _skippedChar, _skippedUi, _skippedNoRenderer, _skippedOther;

        private readonly Dictionary<string, int> _uiByName = new Dictionary<string, int>();

        public void Tick()
        {
            if (Brix.Engine.Architecte.instance == null)
            {
                if (_seen.Count > 0) { _seen.Clear(); _list.Clear(); _culledByName.Clear(); _uiByName.Clear(); _culled = _skippedChar = _skippedUi = _skippedNoRenderer = _skippedOther = 0; }
                return;
            }
            if (Time.realtimeSinceStartup < _nextScan) return;
            _nextScan = Time.realtimeSinceStartup + 20f; // FindObjectsOfType дорогой — реже

            try
            {
                foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>())
                {
                    if (!_seen.Add(a.GetInstanceID())) continue;
                    if (a.cullingMode != AnimatorCullingMode.AlwaysAnimate) { _skippedOther++; continue; }
                    var cv = a.GetComponentInParent<Canvas>();
                    if (cv != null)
                    {
                        _skippedUi++;
                        string uk = Clean(a.gameObject.name) + (cv.rootCanvas != null && cv.rootCanvas.renderMode == RenderMode.WorldSpace ? "(мир)" : "(экран)");
                        int uc; _uiByName.TryGetValue(uk, out uc); _uiByName[uk] = uc + 1;
                        continue;
                    }
                    if (a.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) { _skippedChar++; continue; }
                    if (a.GetComponentInChildren<Renderer>(true) == null) { _skippedNoRenderer++; continue; }

                    if (Enabled) a.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                    _list.Add(a);
                    _culled++;
                    string key = Clean(a.transform.root.name);
                    int c;
                    _culledByName.TryGetValue(key, out c);
                    _culledByName[key] = c + 1;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("AnimatorCulling: " + e.Message); }

            if (Time.realtimeSinceStartup >= _nextLog && _culled > 0)
            {
                _nextLog = Time.realtimeSinceStartup + 120f;
                var top = string.Join(", ", _culledByName.OrderByDescending(kv => kv.Value).Take(10)
                    .Select(kv => kv.Key + "×" + kv.Value).ToArray());
                Plugin.Log.LogInfo(string.Format(
                    "Аниматоры: за кадром экономим на {0} (персонажи {1}, UI {2}, без рендера {3}, уже настроены {4} — не тронуты). Больше всего: {5}",
                    _culled, _skippedChar, _skippedUi, _skippedNoRenderer, _skippedOther, top)
                    + ". Аниматоры интерфейса: " + string.Join(", ", _uiByName.OrderByDescending(kv => kv.Value).Take(10)
                        .Select(kv => kv.Key + "×" + kv.Value).ToArray()));
            }
        }

        private static string Clean(string n)
        {
            int i = n.IndexOf('(');
            n = i > 0 ? n.Substring(0, i) : n;
            return n.Trim();
        }
    }
}
