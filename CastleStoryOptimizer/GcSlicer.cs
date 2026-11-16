using System;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Сборка мусора кусками (порт, Unity 6). Автоматическая сборка Boehm на куче ~1,4 ГБ делает паузу 160–200 мс раз в пару минут.
    /// В Unity 6 есть инкрементальный GC: отключаем автоматическую сборку (GCMode.Manual) и сами вызываем
    /// GarbageCollector.CollectIncremental(срез) по 1–2 мс за кадр, когда куча выросла на TriggerMB. Всё через рефлексию.
    /// Работает только в игре (не в меню и не на загрузке) и только если в сборке игры включён инкрементальный GC;
    /// при любой ошибке или слишком быстром росте кучи возвращает автоматический режим.
    /// </summary>
    internal static class GcSlicer
    {
        public static bool Enabled;
        public static float SliceMs = 2f;
        public static int TriggerMB = 150;
        public static int PanicMB = 700;

        private static bool _inited, _ready, _slicing, _cycle;
        private static PropertyInfo _pMode;
        private static MethodInfo _collect;
        private static object _modeAuto, _modeManual;
        private static readonly object[] Args = new object[1];
        private static long _after, _cycleStartHeap;
        private static int _cycleFrames, _cycles;
        private static double _maxSlice, _cycleMs;
        private static float _cooldownUntil;

        private static void Init()
        {
            _inited = true;
            var t = Type.GetType("UnityEngine.Scripting.GarbageCollector, UnityEngine.CoreModule")
                    ?? Type.GetType("UnityEngine.Scripting.GarbageCollector, UnityEngine");
            if (t == null) { Plugin.Log.LogInfo("GC кусками: тип GarbageCollector не найден (старый Unity) — не включаю."); return; }
            var pInc = t.GetProperty("isIncremental", BindingFlags.Public | BindingFlags.Static);
            _pMode = t.GetProperty("GCMode", BindingFlags.Public | BindingFlags.Static);
            _collect = t.GetMethod("CollectIncremental", BindingFlags.Public | BindingFlags.Static);
            var modeType = t.GetNestedType("Mode");
            var pSlice = t.GetProperty("incrementalTimeSliceNanoseconds", BindingFlags.Public | BindingFlags.Static);
            if (pInc == null || _pMode == null || _collect == null || modeType == null) { Plugin.Log.LogInfo("GC кусками: API не полное — не включаю."); return; }
            bool inc = (bool)pInc.GetValue(null, null);
            object cur = _pMode.GetValue(null, null);
            Plugin.Log.LogInfo("GC: инкрементальный = " + inc + ", режим = " + cur + (pSlice != null ? ", срез по умолчанию " + pSlice.GetValue(null, null) + " нс" : ""));
            if (!inc) { Plugin.Log.LogInfo("GC кусками: в этой сборке игры инкрементальный GC выключен — не включаю."); return; }
            _modeAuto = Enum.ToObject(modeType, 1);   // Enabled
            _modeManual = Enum.ToObject(modeType, 2); // Manual
            _ready = true;
        }

        private static void Start()
        {
            _pMode.SetValue(null, _modeManual, null);
            _slicing = true;
            _cycle = false;
            _after = GC.GetTotalMemory(false);
            Plugin.Log.LogInfo("GC кусками: включён ручной режим, срез " + SliceMs + " мс, запуск при росте кучи на " + TriggerMB + " МБ.");
        }

        public static void Stop(string why)
        {
            if (!_slicing) return;
            try { _pMode.SetValue(null, _modeAuto, null); } catch { }
            _slicing = false; _cycle = false;
            Plugin.Log.LogInfo("GC кусками: автоматический режим возвращён (" + why + ").");
        }

        public static void Tick()
        {
            if (!Enabled) return;
            try
            {
                if (!_inited) Init();
                if (!_ready) return;
                bool inGame = Brix.Engine.Architecte.instance != null && Time.timeSinceLevelLoad > 25f;
                if (!inGame) { Stop("не в игре"); return; }
                if (Time.realtimeSinceStartup < _cooldownUntil) return;
                if (!_slicing) Start();

                long heap = GC.GetTotalMemory(false);
                long growth = heap - _after;
                if (!_cycle)
                {
                    if (growth > (long)PanicMB * 1024 * 1024)
                    {
                        _cooldownUntil = Time.realtimeSinceStartup + 120f;
                        Stop("куча выросла на " + (growth >> 20) + " МБ быстрее, чем успевали срезы");
                        return;
                    }
                    if (growth < (long)TriggerMB * 1024 * 1024) return;
                    _cycle = true; _cycleFrames = 0; _maxSlice = 0; _cycleMs = 0; _cycleStartHeap = heap;
                }

                Args[0] = (ulong)(SliceMs * 1000000.0);
                long t0 = Stopwatch.GetTimestamp();
                bool more = (bool)_collect.Invoke(null, Args);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                _cycleFrames++; _cycleMs += ms; if (ms > _maxSlice) _maxSlice = ms;
                if (!more)
                {
                    _cycle = false; _cycles++;
                    long now = GC.GetTotalMemory(false);
                    _after = now;
                    Plugin.Log.LogInfo("GC кусками: цикл " + _cycles + " за " + _cycleFrames + " кадров, всего " + _cycleMs.ToString("0") + " мс, макс. срез "
                        + _maxSlice.ToString("0.0") + " мс, куча " + (_cycleStartHeap >> 20) + " → " + (now >> 20) + " МБ.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("GC кусками: ошибка — " + e.Message);
                try { Stop("ошибка"); } catch { }
                Enabled = false;
            }
        }
    }
}
