using System;
using System.Linq;
using System.Runtime.InteropServices;
using Brix.NewUI.UserSettings_;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Совместимость старого Unity 5.6 (2017) с современными Windows 11 / мониторами.
    ///
    /// 1) Таймер Windows. С Windows 10 2004 точность таймера задаётся для каждого процесса отдельно,
    ///    по умолчанию 15.6 мс. Ограничитель FPS Unity 5.6 спит через Sleep() — при грубом таймере кадры
    ///    выходят неровными (микрорывки при лимите 120). Просим 1 мс на время работы игры.
    ///
    /// 2) Частота экрана. Игра вызывает Screen.SetResolution(ш, в, полноэкранный) без частоты —
    ///    в эксклюзивном полном экране Unity 5.6 может выбрать 60 Гц на мониторе 144/180 Гц:
    ///    счётчик показывает 120 FPS, а на экран попадает 60. Если так — переключаем на максимальную частоту.
    ///
    /// 3) Сведения о системе в лог (для разбора отчётов с чужих ПК).
    /// </summary>
    internal static class Compat
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private static bool _timer;
        private static bool _fixRefresh;
        private static int _pendingCheckFrame = -1;

        public static void Apply(Harmony h, bool highResTimer, bool fixRefresh)
        {
            _fixRefresh = fixRefresh;
            if (highResTimer)
            {
                try { _timer = timeBeginPeriod(1) == 0; } catch (Exception e) { Plugin.Log.LogWarning("Таймер 1 мс: " + e.Message); }
                if (_timer) Plugin.Log.LogInfo("Совместимость: точность таймера Windows 1 мс (ровнее ограничитель FPS).");
            }
            try
            {
                h.Patch(AccessTools.Method(typeof(UserSettingManager), nameof(UserSettingManager.SetSettingIntoGame)),
                    postfix: new HarmonyMethod(typeof(Compat), nameof(SettingsPostfix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Совместимость: настройки экрана не перехвачены: " + e.Message); }
            _pendingCheckFrame = 30; // и разок после запуска
        }

        public static void Shutdown()
        {
            if (!_timer) return;
            try { timeEndPeriod(1); } catch { }
            _timer = false;
        }

        private static void SettingsPostfix() { _pendingCheckFrame = Time.frameCount + 30; } // смена режима применяется не сразу

        /// <summary>Из Plugin.Update — проверка частоты через несколько кадров после смены режима экрана.</summary>
        public static void Tick()
        {
            if (_pendingCheckFrame < 0 || Time.frameCount < _pendingCheckFrame) return;
            _pendingCheckFrame = -1;
            try { CheckRefresh(); } catch (Exception e) { Plugin.Log.LogWarning("Совместимость: частота экрана: " + e.Message); }
        }

        private static void CheckRefresh()
        {
            var cur = Screen.currentResolution;
            int w = Screen.width, hgt = Screen.height;
            var same = Screen.resolutions.Where(r => r.width == w && r.height == hgt).ToArray();
            int max = same.Length > 0 ? same.Max(r => r.refreshRate) : cur.refreshRate;
            Plugin.Log.LogInfo(string.Format(
                "Экран: {0}x{1} {2}, частота {3} Гц (макс. для этого разрешения {4} Гц), vsync {5}, лимит FPS {6}",
                w, hgt, Screen.fullScreen ? "полный экран" : "окно", cur.refreshRate, max,
                QualitySettings.vSyncCount, Application.targetFrameRate));
            if (_fixRefresh && Screen.fullScreen && max > cur.refreshRate + 1)
            {
                Plugin.Log.LogInfo("Экран работал на " + cur.refreshRate + " Гц вместо " + max + " — переключаю.");
                Screen.SetResolution(w, hgt, true, max);
            }
        }

        public static string SystemSummary()
        {
            return string.Format("Система: {0} ({1} потоков), ОЗУ {2} МБ | {3}, {4} МБ, {5} | {6}",
                SystemInfo.processorType.Trim(), SystemInfo.processorCount, SystemInfo.systemMemorySize,
                SystemInfo.graphicsDeviceName, SystemInfo.graphicsMemorySize, SystemInfo.graphicsDeviceVersion,
                SystemInfo.operatingSystem);
        }
    }
}
