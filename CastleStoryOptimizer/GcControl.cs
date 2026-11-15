using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Пауза сборщика мусора на время загрузки мира.
    ///
    /// Во время загрузки интерфейс создаёт горы временных объектов, и старый сборщик Boehm (Unity 5.6) успевает
    /// сработать ~10 раз подряд по ~1 с каждый (у нас был кадр в 16 с). Mono не даёт выключить GC штатно, но внутри
    /// mono.dll есть функции Boehm GC_disable/GC_enable (счётчик GC_dont_gc). Идею и адреса для этой сборки игры
    /// нашли авторы мода BetterThanCastle (github.com/NNLEKKK/BetterThanCastle); здесь — своя реализация.
    ///
    /// Безопасность: перед вызовом сверяем байты обеих функций (lock; inc/dec одного и того же счётчика) —
    /// другая сборка mono.dll → просто не используем. Пауза снимается в конце загрузки, при ошибке загрузки,
    /// если куча выросла сверх лимита или загрузка идёт слишком долго.
    /// </summary>
    internal static class GcControl
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandle(string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidFn();

        private const int RvaEnable = 0x157848, RvaDisable = 0x157874, RvaDontGc = 0x266BF4;
        // sub rsp,28h; lea rcx,[lock]; call [EnterCriticalSection]; inc/dec dword ptr [GC_dont_gc]
        private static readonly byte[] SigDisable = { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8D, 0x0D, 0x61, 0x74, 0x16, 0x00, 0xFF, 0x15, 0x3B, 0x6A, 0x03, 0x00, 0xFF, 0x05, 0x69, 0xF3, 0x10, 0x00 };
        private static readonly byte[] SigEnable  = { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8D, 0x0D, 0x8D, 0x74, 0x16, 0x00, 0xFF, 0x15, 0x67, 0x6A, 0x03, 0x00, 0xFF, 0x0D, 0x95, 0xF3, 0x10, 0x00 };

        private static VoidFn _disable, _enable;
        private static IntPtr _base;
        private static bool _resolved, _available, _paused;
        private static float _since;

        public static bool Enabled = true;
        public static int MaxHeapMb = 4500;
        public static float MaxSeconds = 150f;
        public static bool Paused { get { return _paused; } }

        private static bool Resolve()
        {
            if (_resolved) return _available;
            _resolved = true;
            try
            {
                var mod = GetModuleHandle("mono");
                if (mod == IntPtr.Zero) { Plugin.Log.LogInfo("Сборщик мусора: mono.dll не найден — пауза при загрузке недоступна."); return false; }
                if (!Match(mod, RvaDisable, SigDisable) || !Match(mod, RvaEnable, SigEnable))
                {
                    Plugin.Log.LogInfo("Сборщик мусора: другая сборка mono.dll — пауза при загрузке не используется.");
                    return false;
                }
                _base = mod;
                _disable = (VoidFn)Marshal.GetDelegateForFunctionPointer(new IntPtr(mod.ToInt64() + RvaDisable), typeof(VoidFn));
                _enable = (VoidFn)Marshal.GetDelegateForFunctionPointer(new IntPtr(mod.ToInt64() + RvaEnable), typeof(VoidFn));
                _available = true;
                Plugin.Log.LogInfo("Сборщик мусора: найдены GC_disable/GC_enable, GC_dont_gc=" + DontGc());
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Сборщик мусора: " + e.Message); return false; }
        }

        private static bool Match(IntPtr b, int rva, byte[] sig)
        {
            for (int i = 0; i < sig.Length; i++)
                if (Marshal.ReadByte(b, rva + i) != sig[i]) return false;
            return true;
        }

        private static int DontGc()
        {
            try { return _base == IntPtr.Zero ? -1 : Marshal.ReadInt32(new IntPtr(_base.ToInt64() + RvaDontGc)); }
            catch { return -1; }
        }

        public static void Pause(string why)
        {
            if (!Enabled || _paused || !Resolve()) return;
            try { _disable(); }
            catch (Exception e) { Plugin.Log.LogWarning("Сборщик мусора: не удалось приостановить: " + e.Message); return; }
            _paused = true;
            _since = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo(string.Format("Сборщик мусора приостановлен ({0}), куча {1} МБ, GC_dont_gc={2}",
                why, GC.GetTotalMemory(false) / (1024 * 1024), DontGc()));
        }

        public static void Resume(string why)
        {
            if (!_paused) return;
            _paused = false;
            try { _enable(); }
            catch (Exception e) { Plugin.Log.LogWarning("Сборщик мусора: не удалось возобновить: " + e.Message); }
            Plugin.Log.LogInfo(string.Format("Сборщик мусора возобновлён ({0}) через {1:0.0} с, куча {2} МБ, GC_dont_gc={3}",
                why, Time.realtimeSinceStartup - _since, GC.GetTotalMemory(false) / (1024 * 1024), DontGc()));
        }

        /// <summary>Из Plugin.Update: страховка от разрастания памяти.</summary>
        public static void Watchdog()
        {
            if (!_paused) return;
            if (GC.GetTotalMemory(false) / (1024 * 1024) > MaxHeapMb) Resume("куча больше " + MaxHeapMb + " МБ");
            else if (Time.realtimeSinceStartup - _since > MaxSeconds) Resume("загрузка дольше " + MaxSeconds + " с");
        }
    }
}
