using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Кнопка «МАКС» рядом с «+» в окне станции Castle Story Plus (список «Производство»): заказывает рецепт столько раз,
    /// сколько влезает в очередь (лимит очереди 20 держит игра/Castle Story Plus на стороне хоста — лишние заказы отбрасываются).
    /// Окно — их C#-интерфейс (CsListRowView): строка целиком кликабельна, «+» — просто картинка внутри строки.
    /// Мы ничего не копируем из их кода: перехватываем создание строки по имени типа и добавляем к ней свой дочерний элемент.
    /// </summary>
    internal static class QueueMax
    {
        private static bool _patched;
        private static float _nextTry;
        private static int _tries;
        private static Harmony _h;

        public static void Apply(Harmony h)
        {
            _h = h;
            TryPatch();
        }

        public static void Tick()
        {
            if (_patched || _h == null || _tries > 15) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextTry) return;
            _nextTry = now + 2f;
            TryPatch();
        }

        private static void TryPatch()
        {
            _tries++;
            try
            {
                var t = AccessTools.TypeByName("CastleStoryPlus.GameUi.CsListRowView");
                var m = t == null ? null : AccessTools.Method(t, "Create");
                if (m == null)
                {
                    if (_tries == 16) Plugin.Log.LogWarning("Кнопка МАКС: строка списка Castle Story Plus не найдена — кнопки не будет.");
                    return;
                }
                _h.Patch(m, postfix: new HarmonyMethod(typeof(QueueMax), nameof(Postfix)));
                _patched = true;
                Plugin.Log.LogInfo("Кнопка «МАКС» в списке производства станций включена.");
            }
            catch (Exception e) { _tries = 99; Plugin.Log.LogWarning("Кнопка МАКС не установлена: " + e.Message); }
        }

        private static string IdOf(object button)
        {
            var tr = Traverse.Create(button);
            string id = null;
            try { id = tr.Property("Id").GetValue<string>(); } catch { }
            if (id == null) try { id = tr.Field("Id").GetValue<string>(); } catch { }
            return id;
        }

        private static void Postfix(Component __result, object button)
        {
            try
            {
                string id = IdOf(button);
                if (__result == null || id == null || !id.StartsWith("operableRecipe")) return;
                var row = __result.transform;
                var add = row.Find("Add");
                var addImg = add == null ? null : add.GetComponent<Image>();
                var addText = add == null ? null : add.GetComponentInChildren<Text>();

                var go = new GameObject("Max", typeof(RectTransform));
                go.transform.SetParent(row, false);
                if (add != null) go.transform.SetSiblingIndex(add.GetSiblingIndex());
                var rt = (RectTransform)go.transform;
                rt.sizeDelta = new Vector2(58f, 26f);
                var le = go.AddComponent<LayoutElement>();
                le.minWidth = 58f; le.preferredWidth = 58f; le.minHeight = 26f; le.preferredHeight = 26f;

                var img = go.AddComponent<Image>();
                if (addImg != null) { img.sprite = addImg.sprite; img.type = addImg.type; }
                img.color = new Color(1f, 0.77f, 0.04f, 1f);
                var btn = go.AddComponent<Button>();
                btn.targetGraphic = img;
                btn.transition = Selectable.Transition.ColorTint;
                var cb = btn.colors;
                cb.normalColor = Color.white; cb.highlightedColor = new Color(1f, 0.93f, 0.6f, 1f); cb.pressedColor = new Color(0.8f, 0.7f, 0.3f, 1f);
                btn.colors = cb;

                var tgo = new GameObject("Text", typeof(RectTransform));
                tgo.transform.SetParent(go.transform, false);
                var trt = (RectTransform)tgo.transform;
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
                var txt = tgo.AddComponent<Text>();
                if (addText != null) txt.font = addText.font;
                else txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                txt.text = "МАКС";
                txt.fontSize = 13;
                txt.fontStyle = FontStyle.Bold;
                txt.alignment = TextAnchor.MiddleCenter;
                txt.color = addText != null ? addText.color : new Color(0.15f, 0.1f, 0f);
                txt.raycastTarget = false;

                var click = AccessTools.Method(button.GetType(), "Click");
                btn.onClick.AddListener(delegate
                {
                    try
                    {
                        if (click == null) return;
                        var args = new object[] { false, false };
                        for (int i = 0; i < 20; i++) click.Invoke(button, args);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("МАКС: " + e.Message); }
                });
            }
            catch (Exception e) { Plugin.Log.LogWarning("Кнопка МАКС: строка без кнопки: " + e.Message); }
        }
    }
}
