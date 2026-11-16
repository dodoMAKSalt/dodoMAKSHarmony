using System;
using System.Collections;
using System.Reflection;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using Brix.UI.Icons;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Раздел «Бригада» в меню приказов Castle Story Plus (Цели / Типы ресурсов / Приоритет / Действия): кнопки − и +
    /// меняют максимум бриктронов на выбранной задаче (Shift — сразу на 5), посередине «сейчас/максимум».
    /// Меню — их C#-интерфейс: перехватываем GamePopupBuilder.Build по имени типа и добавляем раздел через рефлексию.
    /// Изменение уходит игровыми командами CmdAddProjectCapacity/CmdRemoveProjectCapacity (хост применяет и синхронизирует).
    /// Потолок — WorldSettings.CrewLimit() (в игре было 8, теперь 30 или своё значение из настроек мира).
    /// </summary>
    internal static class CrewMenu
    {
        private static bool _patched;
        private static float _nextTry;
        private static int _tries;
        private static Harmony _h;
        private static PropertyInfo _selProp;

        public static void Apply(Harmony h) { _h = h; TryPatch(); }

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
                var t = AccessTools.TypeByName("CastleStoryPlus.GameUi.GamePopupBuilder");
                var m = t == null ? null : AccessTools.Method(t, "Build");
                if (m == null)
                {
                    if (_tries == 16) Plugin.Log.LogWarning("Бригада в меню приказов: GamePopupBuilder не найден — раздела не будет.");
                    return;
                }
                _h.Patch(m, postfix: new HarmonyMethod(typeof(CrewMenu), nameof(Postfix)));
                _patched = true;
                Plugin.Log.LogInfo("Раздел «Бригада» в меню приказов включён.");
            }
            catch (Exception e) { _tries = 99; Plugin.Log.LogWarning("Бригада в меню приказов не установлена: " + e.Message); }
        }

        private static Project Sel()
        {
            try
            {
                if (_selProp == null)
                    _selProp = AccessTools.Property(AccessTools.TypeByName("CastleStoryPlus.GameUi.GameUiState"), "Selected");
                var p = _selProp.GetValue(null, null) as Project;
                return p != null ? p : null;
            }
            catch { return null; }
        }

        private static void SetMember(object obj, string name, object value)
        {
            var tr = Traverse.Create(obj);
            if (tr.Field(name).FieldExists()) tr.Field(name).SetValue(value);
            else tr.Property(name).SetValue(value);
        }

        private static object GetMember(object obj, string name)
        {
            var tr = Traverse.Create(obj);
            return tr.Field(name).FieldExists() ? tr.Field(name).GetValue() : tr.Property(name).GetValue();
        }

        private static void Change(int delta)
        {
            var p = Sel();
            var u = User.LocalUser;
            if (p == null || u == null) return;
            var net = u.GetComponent<UNetGroupe>();
            if (net == null) return;
            int n = Math.Abs(delta);
            for (int i = 0; i < n; i++)
            {
                if (delta > 0) { if (p.MaximumCrewSize + i >= WorldSettings.CrewLimit()) break; net.CallCmdAddProjectCapacity(p.gameObject); }
                else { if (p.MaximumCrewSize - i <= 1) break; net.CallCmdRemoveProjectCapacity(p.gameObject); }
            }
        }

        private static object MakeButton(Type btnType, string id, Func<string> label, Func<Sprite> icon, Func<Color> color,
            Func<string> count, Func<bool> enabled, Action<bool, bool> click)
        {
            var b = Activator.CreateInstance(btnType, true);
            SetMember(b, "Id", id);
            SetMember(b, "Label", label);
            SetMember(b, "Icon", icon);
            SetMember(b, "IconColor", color);
            if (count != null) SetMember(b, "Count", count);
            if (enabled != null) SetMember(b, "Enabled", enabled);
            if (click != null) SetMember(b, "OnClick", click);
            return b;
        }

        private static void Postfix(object __result)
        {
            try
            {
                if (__result == null) return;
                var panelType = __result.GetType();
                var prio = panelType.GetMethod("Find").Invoke(__result, new object[] { "priority" });
                var section = panelType.GetMethod("SectionAfter").Invoke(__result,
                    new object[] { "priority", "crew", (Func<string>)(() => "Бригада") });
                if (prio != null) SetMember(section, "Visible", GetMember(prio, "Visible"));
                var items = (IList)GetMember(section, "Items");
                var btnType = AccessTools.TypeByName("CastleStoryPlus.GameUi.UiButton");
                var yellow = new Color(1f, 0.77f, 0.04f, 1f);
                var gray = new Color(0.5f, 0.5f, 0.5f, 1f);

                items.Add(MakeButton(btnType, "crewLower", () => "Меньше бригада (Shift: −5)",
                    () => IconKeys.UI_Minus.Get64(),
                    () => { var p = Sel(); return p != null && p.MaximumCrewSize > 1 ? yellow : gray; },
                    null, () => { var p = Sel(); return p != null && p.MaximumCrewSize > 1; },
                    (shift, ctrl) => Change(shift ? -5 : -1)));
                items.Add(MakeButton(btnType, "crewInfo", () => "Бригада: сейчас / максимум",
                    () => IconKeys.UI_Flag.Get64(), () => yellow,
                    () => { var p = Sel(); return p == null ? "-" : p.CrewCount() + "/" + p.MaximumCrewSize; },
                    null, null));
                items.Add(MakeButton(btnType, "crewRaise", () => "Больше бригада (Shift: +5)",
                    () => IconKeys.UI_Plus.Get64(),
                    () => { var p = Sel(); return p != null && p.MaximumCrewSize < WorldSettings.CrewLimit() ? yellow : gray; },
                    null, () => { var p = Sel(); return p != null && p.MaximumCrewSize < WorldSettings.CrewLimit(); },
                    (shift, ctrl) => Change(shift ? 5 : 1)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Бригада в меню приказов: раздел не добавлен: " + e.Message); }
        }
    }
}
