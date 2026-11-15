using System;
using System.IO;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Работа на порте Castle Story под Unity 6 вместе с модом Castle Story Plus
    /// (github.com/Tricky12321/CastleStoryPlus, GPL v3 — их код не копируем).
    ///
    /// Castle Story Plus сам делает почти всё, что делал наш мод для старой игры (умные рабочие, бронь задач и ресурсов,
    /// выкапывание застрявших, быстрая загрузка, C#-меню вместо Lua, короткие сборки мусора, аниматоры, звук, плашки,
    /// быстрый выход). На порте мы включаем только то, чего у них нет: русский перевод, приоритеты мастерских,
    /// замерщик (клавиши с Shift — F7/F8/F9 у них свои) и мелкие ускорения, которых у них нет.
    /// </summary>
    internal static class PortCompat
    {
        public const string CspGuid = "com.tricky12321.castlestoryplus";

        /// <summary>Игра работает на Unity 6 (порт).</summary>
        public static bool Unity6 { get; private set; }
        /// <summary>Установлен мод Castle Story Plus.</summary>
        public static bool Csp { get; private set; }
        /// <summary>Режим совместной работы: порт или Castle Story Plus.</summary>
        public static bool Active { get { return Unity6 || Csp; } }

        public static void Detect()
        {
            try { Unity6 = Application.unityVersion.StartsWith("6"); } catch { Unity6 = false; }
            try
            {
                Csp = Directory.GetFiles(BepInEx.Paths.PluginPath, "CastleStoryPlus.dll", SearchOption.AllDirectories).Length > 0;
            }
            catch { Csp = false; }
            if (Active)
                Plugin.Log.LogInfo("Порт/Castle Story Plus: Unity " + Application.unityVersion + (Csp ? ", Castle Story Plus найден" : "")
                    + " — включаю только то, чего нет у них.");
        }

        /// <summary>Включить функцию: по конфигу, а в режиме порта — только если её нет у Castle Story Plus.</summary>
        public static bool On(bool cfg, bool keepOnPort)
        {
            return cfg && (!Active || keepOnPort);
        }

        /// <summary>Клавиши замерщика: на порте с Shift (F7/F8/F9 заняты окнами Castle Story Plus).</summary>
        public static bool KeyDown(KeyCode k)
        {
            if (!Input.GetKeyDown(k)) return false;
            if (!Active) return true;
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        }
    }
}
