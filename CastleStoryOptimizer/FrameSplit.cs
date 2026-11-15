using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Делит кадр на части.
    ///   «процессор занят» = от первого скрипта кадра до конца кадра (WaitForEndOfFrame);
    ///   «ожидание»        = от конца кадра до первого скрипта следующего: видеокарта (Present), vsync, лимит FPS.
    ///
    /// Этапы внутри «процессора» (порядок кадра Unity):
    ///   1. FixedUpdate + физика   : первый FixedUpdate → первый Update
    ///   2. Update + корутины + анимации : первый Update → первый LateUpdate
    ///   3. LateUpdate + перестройка UI   : первый LateUpdate → первая камера
    ///   4. Рендер                 : первая камера → конец последней камеры
    ///   5. После рендера          : OnGUI (наш оверлей тоже), конец кадра
    /// </summary>
    internal static class FrameSplit
    {
        public static long FrameFirst, FirstUpdate, FirstLate, FirstRender, LastRenderEnd;
        public static bool FixedSeen;
        public static long FixedScriptTicks; // сумма Unity-FixedUpdate скриптов за кадр
        private static Entry _sFixedScripts, _sPhysics;

        private static Entry _busy, _wait, _sFixed, _sUpdate, _sLate, _sRender, _sTail;
        private static bool _enabled;
        private static long _prevEnd;
        private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

        public static void Install(Entry busy, Entry wait, bool enabled)
        {
            _busy = busy;
            _wait = wait;
            _enabled = enabled;
            if (!enabled) return;
            _sFixed = Stage("1. FixedUpdate + физика");
            _sFixedScripts = Stage("1а. скрипты FixedUpdate");
            _sPhysics = Stage("1б. физика PhysX + колбэки + WaitForFixedUpdate");
            _sUpdate = Stage("2. Update + корутины + анимации");
            _sLate = Stage("3. LateUpdate + перестройка UI");
            _sRender = Stage("4. Рендер (все камеры)");
            _sTail = Stage("5. После рендера (OnGUI)");
        }

        private static Entry Stage(string name)
        {
            var e = new Entry { Name = "[Этап] " + name, Category = "Stage" };
            ProfilerPatches.All.Add(e);
            return e;
        }

        public static IEnumerator Loop()
        {
            if (!_enabled) yield break;
            while (true)
            {
                yield return EndOfFrame;
                long now = Stopwatch.GetTimestamp();
                if (FrameFirst != 0 && _prevEnd != 0 && FrameFirst > _prevEnd)
                {
                    Add(_wait, FrameFirst - _prevEnd);
                    Add(_busy, now - FrameFirst);

                    if (FixedSeen && FirstUpdate > FrameFirst)
                    {
                        long stage1 = FirstUpdate - FrameFirst;
                        Add(_sFixed, stage1);
                        Add(_sFixedScripts, FixedScriptTicks);
                        Add(_sPhysics, System.Math.Max(0, stage1 - FixedScriptTicks));
                    }
                    if (FirstUpdate != 0 && FirstLate > FirstUpdate) Add(_sUpdate, FirstLate - FirstUpdate);
                    if (FirstLate != 0 && FirstRender > FirstLate) Add(_sLate, FirstRender - FirstLate);
                    if (FirstRender != 0 && LastRenderEnd > FirstRender) Add(_sRender, LastRenderEnd - FirstRender);
                    if (LastRenderEnd != 0 && now > LastRenderEnd) Add(_sTail, now - LastRenderEnd);
                }
                _prevEnd = now;
                FrameFirst = FirstUpdate = FirstLate = FirstRender = LastRenderEnd = 0;
                FixedSeen = false;
                FixedScriptTicks = 0;
            }
        }

        private static void Add(Entry e, long dt)
        {
            e.Ticks += dt;
            e.FrameTicks += dt;
            e.Calls++;
            if (dt > e.MaxTicks) e.MaxTicks = dt;
            if (LoadingPatches.IsLoading) { e.LoadTicks += dt; e.LoadCalls++; }
        }
    }
}
