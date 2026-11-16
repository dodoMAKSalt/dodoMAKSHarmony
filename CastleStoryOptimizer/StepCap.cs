using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Лавина кадров: шаг физики 0,02 с, а «макс. догон» Unity 0,33 с — за один тяжёлый кадр игра делает до 16 шагов
    /// FixedUpdate (в замерах со 100 бриктронами до ~1100 вызовов Behavior.BrixFixedUpdate за кадр), следующий кадр
    /// ещё тяжелее — FPS падает с 200 до 10. Ограничиваем число шагов физики за кадр (по умолчанию 3):
    /// при очень тяжёлом кадре игра чуть замедлится, но не уйдёт в «спираль смерти».
    ///
    /// Действует ТОЛЬКО в игре на карте (после конца загрузки катки). На заставке, в меню и во время загрузки значение
    /// игры не трогаем: там кадры по секунде, урезанный «догон» замедляет игровое время и логотипы заставки
    /// (анимации по времени против корутин по реальному) налезали друг на друга.
    /// Не трогаем и при ускорении времени Castle Story Plus (timeScale &gt; 1) — у них своё управление этим значением.
    /// </summary>
    internal static class StepCap
    {
        private static int _steps;
        private static float _next;
        private static float _original = -1f;
        private static bool _inGame;
        private static bool _applied;

        public static void Init(int steps)
        {
            _steps = Mathf.Clamp(steps, 0, 16);
            _original = Time.maximumDeltaTime;
            if (_steps <= 0) return;
            SceneManager.sceneLoaded += delegate { _inGame = false; Restore(); }; // новая сцена (меню, карта) — игровое значение
            Plugin.Log.LogInfo(string.Format("Ограничитель шагов физики: в игре не больше {0} за кадр (в меню и загрузке — как в игре, макс. догон {1:0.00} с).", _steps, _original));
        }

        /// <summary>Вызывается в конце загрузки катки: дальше идёт игра.</summary>
        public static void OnLevelLoaded() { if (_steps > 0) _inGame = true; }

        private static void Restore()
        {
            if (!_applied) return;
            _applied = false;
            try { if (_original > 0f) Time.maximumDeltaTime = _original; } catch { }
        }

        public static void Tick()
        {
            if (_steps <= 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 1f;
            try
            {
                if (!_inGame || LoadingPatches.IsLoading || Time.timeScale > 1.01f) { Restore(); return; }
                float want = Time.fixedDeltaTime * _steps;
                if (!_applied) { _original = Time.maximumDeltaTime; Plugin.Log.LogInfo(string.Format("Макс. догон физики в игре: {0:0.000} с ({1} шагов).", want, _steps)); }
                Time.maximumDeltaTime = want;
                _applied = true;
            }
            catch (Exception e) { _steps = 0; Plugin.Log.LogWarning("Ограничитель шагов физики выключен: " + e.Message); }
        }
    }
}
