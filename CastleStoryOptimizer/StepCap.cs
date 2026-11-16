using System;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Лавина кадров: шаг физики 0,02 с, а «макс. догон» Unity 0,33 с — за один тяжёлый кадр игра делает до 16 шагов
    /// FixedUpdate (в замерах со 100 бриктронами до ~1100 вызовов Behavior.BrixFixedUpdate за кадр), следующий кадр
    /// ещё тяжелее — FPS падает с 200 до 10. Ограничиваем число шагов физики за кадр (по умолчанию 3):
    /// при очень тяжёлом кадре игра чуть замедлится, но не уйдёт в «спираль смерти».
    /// Не трогаем, пока ускорение времени Castle Story Plus (timeScale &gt; 1) — у них своё управление этим значением.
    /// </summary>
    internal static class StepCap
    {
        private static int _steps;
        private static float _next;
        private static float _last = -1f;

        public static void Init(int steps)
        {
            _steps = Mathf.Clamp(steps, 0, 16);
            if (_steps > 0)
                Plugin.Log.LogInfo(string.Format("Ограничитель шагов физики: не больше {0} за кадр (было макс. догон {1:0.00} с).", _steps, Time.maximumDeltaTime));
        }

        public static void Tick()
        {
            if (_steps <= 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 1f;
            try
            {
                if (Time.timeScale > 1.01f) return; // ускорение времени: значением управляет Castle Story Plus
                float want = Time.fixedDeltaTime * _steps;
                if (Mathf.Abs(Time.maximumDeltaTime - want) > 0.0005f)
                {
                    Time.maximumDeltaTime = want;
                    if (Mathf.Abs(_last - want) > 0.0005f) Plugin.Log.LogInfo(string.Format("Макс. догон физики установлен: {0:0.000} с ({1} шагов).", want, _steps));
                    _last = want;
                }
            }
            catch (Exception e) { _steps = 0; Plugin.Log.LogWarning("Ограничитель шагов физики выключен: " + e.Message); }
        }
    }
}
