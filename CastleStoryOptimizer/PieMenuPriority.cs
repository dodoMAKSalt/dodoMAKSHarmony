using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Brix.Game.Components;
using Brix.Legacy;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Кнопка «Приоритет» в круговом меню каждой мастерской (ПКМ по печи/верстаку и т.д.).
    ///
    /// Круговое меню игры описано в Info/Livre/PieMenu_Menus.txt и PieMenu_Buttons.txt,
    /// действия кнопок — словарь Instructions.commendes по имени.
    /// Мы НЕ меняем файлы игры: при чтении этих файлов (Livre.Lire → File.ReadAllText)
    /// подмешиваем в память своё подменю, а действия регистрируем в словаре.
    /// </summary>
    internal static class PieMenuPriority
    {
        private const string Hub = "dodo_PriorityHub";
        private static readonly int[] Order = { 5, 4, 3, 2, 1, 0 }; // по кругу: СРОЧНО → Выключена
        // Только иконки, которые точно есть в обычной игре (используются кнопками игровых меню):
        // UI_X — «Снять выделение», Lock/Unlock — двери, UI_Check — «Применить», Target — катапульта, UI_CallToArms — «К оружию».
        // Radius_++/-- из старой кнопки радиуса в игре НЕ загружены — кнопка без иконки становится пустым слотом.
        private static readonly string[] Icons = { "UI_X", "Lock", "Unlock", "UI_Check", "Target", "UI_CallToArms" };
        private static bool _injected;

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Livre), nameof(Livre.Lire), new[] { typeof(string), typeof(string) }),
                    transpiler: new HarmonyMethod(typeof(PieMenuPriority), nameof(LireTranspiler)));
                h.Patch(AccessTools.Method(typeof(Instructions), nameof(Instructions.Batire)),
                    postfix: new HarmonyMethod(typeof(PieMenuPriority), nameof(RegisterCommands)));
                RegisterCommands();
                Plugin.Log.LogInfo("Приоритет в круговом меню мастерских включён.");
            }
            catch (Exception e) { Plugin.Log.LogError("Круговое меню приоритетов не включено: " + e); }
        }

        // ---------- подмешивание текста ----------
        private static IEnumerable<CodeInstruction> LireTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var readAll = AccessTools.Method(typeof(File), nameof(File.ReadAllText), new[] { typeof(string) });
            var ours = AccessTools.Method(typeof(PieMenuPriority), nameof(ReadText));
            var theirs = BtcCompat.BtcLivreRead; // BetterThanCastle мог уже подменить вызов на свой
            foreach (var ins in instructions)
            {
                if (ins.Calls(readAll) || (theirs != null && ins.Calls(theirs)))
                    yield return new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(ins);
                else yield return ins;
            }
        }

        public static string ReadText(string path)
        {
            var text = BtcCompat.ReadLivre(path); // с добавками BetterThanCastle, если он установлен
            try
            {
                if (path.EndsWith("PieMenu_Buttons.txt", StringComparison.OrdinalIgnoreCase)) return text + ButtonsText();
                if (path.EndsWith("PieMenu_Menus.txt", StringComparison.OrdinalIgnoreCase)) return InjectMenus(text);
            }
            catch (Exception e) { Plugin.Log.LogError("Не удалось дополнить круговое меню: " + e); }
            return text;
        }

        private static string ButtonsText()
        {
            var sb = new StringBuilder("\r\n\r\n");
            // Формат как у кнопок исключений склада (они в подменю работают):
            // без Label/Toggle — иначе игра берёт «числовой»/«переключатель» шаблон кнопки, который в подменю глючит.
            // Текущий уровень — Condition = false → иконка затемнена и не нажимается.
            sb.Append("\tBUTTON ").Append(Hub).Append("\r\n")
              .Append("\tName : ##dodo_priority_hub\r\n")
              .Append("\tIcon : Gear\r\n")
              .Append("\tTooltip : ##dodo_priority_hub_text\r\n\r\n");
            for (int l = 0; l < 6; l++)
            {
                sb.Append("\tBUTTON dodo_Priority_").Append(l).Append("\r\n")
                  .Append("\tName : ##dodo_priority_").Append(l).Append("\r\n")
                  .Append("\tEffect: Event_dodo_Priority_").Append(l).Append("\r\n")
                  .Append("\tCondition : Condition_dodo_NotCurrent_").Append(l).Append("\r\n")
                  .Append("\tIcon : ").Append(Icons[l]).Append("\r\n")
                  .Append("\tTooltip : ##dodo_priority_").Append(l).Append("_text\r\n\r\n");
            }
            return sb.ToString();
        }

        /// <summary>В каждом MENU мастерской (там есть Crafting_DoNothing) заменяем последний Hole на подменю.</summary>
        private static string InjectMenus(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            int menus = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].TrimStart().StartsWith("MENU ")) continue;
                int end = i + 1;
                while (end < lines.Count && !lines[end].TrimStart().StartsWith("MENU ")) end++;
                int doNothing = -1;
                for (int j = i + 1; j < end; j++) if (lines[j].Contains("Crafting_DoNothing")) doNothing = j;
                if (doNothing < 0) continue;
                int hole = -1;
                for (int j = doNothing - 1; j > i; j--) if (lines[j].Trim() == "Hole") { hole = j; break; }
                if (hole < 0) continue;

                var block = new List<string> { "\tSubmenu: " + Hub, "\t\tHole" };
                foreach (int l in Order) block.Add("\t\tButton: dodo_Priority_" + l);
                block.Add("\t\tHole");
                block.Add("\tEnd");
                lines.RemoveAt(hole);
                lines.InsertRange(hole, block);
                menus++;
                i = hole + block.Count;
            }
            _injected = menus > 0;
            Plugin.Log.LogInfo("Круговое меню: кнопка приоритета добавлена в " + menus + " мастерских.");
            return string.Join("\r\n", lines.ToArray());
        }

        // ---------- действия ----------
        private static void RegisterCommands()
        {
            for (int l = 0; l < 6; l++)
            {
                int level = l;
                Instructions.commendes["Event_dodo_Priority_" + level] = () =>
                {
                    foreach (var st in MenuStations()) StationPriorities.RequestSet(st, level);
                };
                // false → кнопка текущего уровня затемнена
                Instructions.conditions["Condition_dodo_NotCurrent_" + level] = () =>
                {
                    var st = MenuStations().FirstOrDefault();
                    return st == null || StationPriorities.GetLevel(st) != level;
                };
            }
            Instructions.conditions["Condition_dodo_PriorityLabel"] = () =>
            {
                var st = MenuStations().FirstOrDefault();
                return st == null ? string.Empty : StationPriorities.NameOf(StationPriorities.GetLevel(st));
            };
        }

        /// <summary>Мастерские, для которых открыто меню: выделенные (sujets) или та, по которой кликнули (objet).</summary>
        private static List<CraftingStation> MenuStations()
        {
            var list = new List<CraftingStation>();
            if (Instructions.sujets != null)
                foreach (var mb in Instructions.sujets)
                {
                    var st = mb == null ? null : mb.GetComponent<CraftingStation>();
                    if (st != null && !list.Contains(st)) list.Add(st);
                }
            if (list.Count == 0)
            {
                var go = Instructions.objet as GameObject;
                var st = go == null ? null : go.GetComponent<CraftingStation>();
                if (st != null) list.Add(st);
            }
            return list;
        }

        public static bool Injected => _injected;
    }
}
