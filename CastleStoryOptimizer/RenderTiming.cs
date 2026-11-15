using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Время рендера на главном потоке: от Camera.onPreCull до Camera.onPostRender для каждой камеры.
    /// Это то, что не видно в Update-методах: отсечение, сортировка, отправка draw call'ов.
    /// </summary>
    internal static class RenderTiming
    {
        private static Entry _entry;
        private static readonly Dictionary<Camera, long> Started = new Dictionary<Camera, long>();

        public static void Install(Entry entry)
        {
            _entry = entry;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
        }

        private static void OnPreCull(Camera cam)
        {
            long now = Stopwatch.GetTimestamp();
            Started[cam] = now;
            if (FrameSplit.FirstRender == 0) FrameSplit.FirstRender = now;
        }

        private static void OnPostRender(Camera cam)
        {
            long start;
            if (!Started.TryGetValue(cam, out start) || start == 0) return;
            Started[cam] = 0;
            long end = Stopwatch.GetTimestamp();
            long dt = end - start;
            FrameSplit.LastRenderEnd = end;
            _entry.Ticks += dt;
            _entry.FrameTicks += dt;
            _entry.Calls++;
            if (LoadingPatches.IsLoading) { _entry.LoadTicks += dt; _entry.LoadCalls++; }
            if (dt > _entry.MaxTicks) _entry.MaxTicks = dt;
        }
    }
}
