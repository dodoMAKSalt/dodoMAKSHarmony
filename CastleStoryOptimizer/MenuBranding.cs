using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Brix.UI.MainMenu;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Надпись с названием мода под логотипом Castle Story в главном меню.
    /// Логотип — обычная Unity-картинка (Image/RawImage) со словом "logo" в имени спрайта/объекта.
    /// Нашли — вешаем дочерний Text под ним (масштабируется вместе с UI).
    /// Не нашли — рисуем через OnGUI, пока открыто меню.
    /// </summary>
    internal sealed class MenuBranding
    {
        public const string Title = "dodoMAKSharmony";

        private float _nextScan;
        private GameObject _label;
        private bool _loggedCandidates;
        private GUIStyle _style;

        public bool InMenu { get; private set; }

        public void Tick()
        {
            if (Time.realtimeSinceStartup < _nextScan) return;
            _nextScan = Time.realtimeSinceStartup + (_label != null ? 5f : 1.5f);

            // FindObjectOfType перебирает ВСЕ объекты сцены (в катке ~11 тыс. — десятки мс).
            // Раньше это делалось и в игре раз в 1.5 с — отсюда регулярный фриз. Теперь только в меню.
            // Надпись уже висит на логотипе — значит меню открыто (логотип исчезает вместе с меню), искать не нужно:
            // FindObjectOfType стоил ~24 мс в меню каждые 1.5 с.
            if (_label != null) InMenu = !LoadingPatches.IsLoading && Brix.Engine.Architecte.instance == null;
            else InMenu = !LoadingPatches.IsLoading && Brix.Engine.Architecte.instance == null
                     && UnityEngine.Object.FindObjectOfType<MainMenuMusic>() != null;
            if (_version != null) _version.SetActive(InMenu);
            if (_label != null || !InMenu) return;

            var logo = FindLogo();
            if (logo != null)
            {
                Attach(logo);
                AttachVersion(logo);
            }
        }

        private GameObject _version;

        /// <summary>Версия мода в правом нижнем углу экрана меню (на корневом Canvas логотипа).</summary>
        private void AttachVersion(RectTransform logo)
        {
            var canvas = logo.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var root = canvas.rootCanvas.transform;

            _version = new GameObject("dodoMAKSharmony_Version", typeof(RectTransform));
            var rt = (RectTransform)_version.transform;
            rt.SetParent(root, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(-12f, 8f);
            rt.sizeDelta = new Vector2(420f, 28f);
            rt.SetAsLastSibling();

            var text = _version.AddComponent<Text>();
            text.text = Title + " v" + Plugin.Version;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 18;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.LowerRight;
            text.color = new Color(1f, 0.77f, 0.04f, 0.9f);
            text.raycastTarget = false;
            var shadow = _version.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(2f, -2f);
        }

        private RectTransform FindLogo()
        {
            StringBuilder names = _loggedCandidates ? null : new StringBuilder();
            foreach (var g in UnityEngine.Object.FindObjectsOfType<Graphic>())
            {
                string n = g.name;
                var img = g as Image;
                var raw = g as RawImage;
                if (img != null && img.sprite != null) n += "/" + img.sprite.name;
                if (raw != null && raw.texture != null) n += "/" + raw.texture.name;
                if (names != null && (img != null || raw != null)) names.Append(n).Append("; ");
                if (n.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0 && g.rectTransform.rect.width > 100)
                    return g.rectTransform;
            }
            if (names != null)
            {
                _loggedCandidates = true;
                Plugin.Log.LogInfo("Логотип не найден. Картинки в меню: " + names);
            }
            return null;
        }

        private void Attach(RectTransform logo)
        {
            _label = new GameObject("dodoMAKSharmony_Label", typeof(RectTransform));
            var rt = (RectTransform)_label.transform;
            rt.SetParent(logo, false);
            // Под логотипом, по центру, ширина = ширине логотипа
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -4f);
            rt.sizeDelta = new Vector2(0f, Mathf.Max(30f, logo.rect.height * 0.22f));

            var text = _label.AddComponent<Text>();
            text.text = Title;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.UpperCenter;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 10;
            text.resizeTextMaxSize = 64;
            text.color = new Color(1f, 0.77f, 0.04f); // жёлтый, как кнопки меню
            text.raycastTarget = false;

            var outline = _label.AddComponent<Outline>();
            outline.effectColor = new Color(0.25f, 0.12f, 0f, 1f);
            outline.effectDistance = new Vector2(2f, -2f);
            var shadow = _label.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
            shadow.effectDistance = new Vector2(3f, -4f);

            Plugin.Log.LogInfo("Надпись мода прикреплена к логотипу: " + logo.name);
        }

        /// <summary>Запасной вариант, если логотип в UI не найден.</summary>
        public void DrawFallback()
        {
            if (_label != null || !InMenu) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = Mathf.Max(16, Screen.height / 28)
                };
            }
            var r = new Rect(0, Screen.height * 0.30f, Screen.width, Screen.height / 18f);
            _style.normal.textColor = new Color(0, 0, 0, 0.7f);
            GUI.Label(new Rect(r.x + 3, r.y + 3, r.width, r.height), Title, _style);
            _style.normal.textColor = new Color(1f, 0.77f, 0.04f);
            GUI.Label(r, Title, _style);

            // Версия в правом нижнем углу (запасной вариант)
            var old = _style.alignment;
            int oldSize = _style.fontSize;
            _style.alignment = TextAnchor.LowerRight;
            _style.fontSize = Mathf.Max(12, Screen.height / 60);
            var vr = new Rect(0, Screen.height - 40, Screen.width - 12, 32);
            GUI.Label(vr, Title + " v" + Plugin.Version, _style);
            _style.alignment = old;
            _style.fontSize = oldSize;
        }
    }
}
