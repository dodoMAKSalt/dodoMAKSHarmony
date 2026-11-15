using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Brix.UI.Builder.Menu.Component;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Загрузка катки «стоит» на ~71% — это фаза StartStep: строится весь интерфейс на Lua.
    ///
    /// 1) Ускорение: меню строятся Lua-корутиной, которую игра продвигает РОВНО на один шаг за кадр
    ///    (LuaMenuComponent.DoCoroutine), а кадр во время загрузки дорогой (за экраном уже живёт весь мир).
    ///    Делаем несколько шагов за кадр в пределах бюджета времени: 100 мс при загрузке, 4 мс в игре.
    /// 2) Диагностика: время каждого Lua-файла (DoFile/LoadFile, LuaLoader.Load) — включительно,
    ///    и топ всего, что работало во время загрузки, в отчёт после загрузки.
    /// </summary>
    internal static class LoadingDiagnostics
    {
        private static readonly Dictionary<string, Entry> ByKey = new Dictionary<string, Entry>();
        private static float _budgetLoadingMs = 100f, _budgetGameMs = 4f;
        private static AccessTools.FieldRef<LuaMenuComponent, Func<DynValue, LuaMenuComponent, bool>> _isStop;
        private static AccessTools.FieldRef<LuaMenuComponent, DynValue> _tMenu;
        private static readonly WaitForEndOfFrame Eof = new WaitForEndOfFrame();
        [ThreadStatic] private static int _fileDepth;

        public static void Apply(Harmony h, bool fastMenus, bool diagnostics)
        {
            if (fastMenus)
            {
                try
                {
                    _isStop = AccessTools.FieldRefAccess<LuaMenuComponent, Func<DynValue, LuaMenuComponent, bool>>("_isStop");
                    _tMenu = AccessTools.FieldRefAccess<LuaMenuComponent, DynValue>("_tMenu");
                    h.Patch(AccessTools.Method(typeof(LuaMenuComponent), nameof(LuaMenuComponent.DoCoroutine)),
                        prefix: new HarmonyMethod(typeof(LoadingDiagnostics), nameof(DoCoroutinePrefix)));
                    Plugin.Log.LogInfo("Быстрое построение Lua-меню включено (несколько шагов за кадр).");
                }
                catch (Exception e) { Plugin.Log.LogError("Быстрое построение меню не включено: " + e); }
            }

            if (diagnostics)
            {
                var pre = new HarmonyMethod(typeof(LoadingDiagnostics), nameof(FilePrefix));
                var post = new HarmonyMethod(typeof(LoadingDiagnostics), nameof(FilePostfix));
                int n = 0;
                foreach (var m in typeof(Script).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if ((m.Name == "DoFile" || m.Name == "LoadFile") && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string))
                    { try { h.Patch(m, prefix: pre, postfix: post); n++; } catch { } }
                }
                foreach (var m in typeof(Brix.Lua.LuaLoader).GetMethods(BindingFlags.Static | BindingFlags.Public))
                {
                    if (m.Name == "Load" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string))
                    { try { h.Patch(m, prefix: pre, postfix: post); n++; } catch { } }
                }
                Plugin.Log.LogInfo("Замер Lua-файлов: перехвачено методов " + n);
            }
        }

        // ---------- быстрые меню ----------
        private static bool DoCoroutinePrefix(LuaMenuComponent __instance, ref IEnumerator __result)
        {
            __result = FastCoroutine(__instance);
            return false;
        }

        private static IEnumerator FastCoroutine(LuaMenuComponent self)
        {
            LuaMenuComponent.CoroutineCount++;
            var sw = new Stopwatch();
            try
            {
                while (true)
                {
                    sw.Reset(); sw.Start();
                    float budget = LoadingPatches.IsLoading ? _budgetLoadingMs : _budgetGameMs;
                    int maxSteps = LoadingPatches.IsLoading ? 64 : 8;
                    bool stop = false;
                    for (int step = 0; step < maxSteps; step++)
                    {
                        var isStop = _isStop(self);
                        if (isStop == null) { stop = true; break; }
                        double before = sw.Elapsed.TotalMilliseconds;
                        try { stop = isStop(_tMenu(self), self); }
                        catch (Exception e) { Brix.Lua.LuaLibs.LuaExceptionCatcher(e); stop = true; }
                        if (stop) break;
                        // Шаг почти ничего не сделал — корутина чего-то ждёт (WaitForCondition и т.п.):
                        // не крутим её вхолостую, отпускаем до следующего кадра.
                        if (sw.Elapsed.TotalMilliseconds - before < 0.1) break;
                        if (sw.Elapsed.TotalMilliseconds >= budget) break;
                    }
                    if (stop) break;
                    yield return Eof;
                }
            }
            finally
            {
                LuaMenuComponent.CoroutineCount--;
                if (self != null) _isStop(self) = null;
            }
        }

        // ---------- замер Lua-файлов ----------
        private static void FilePrefix(MethodBase __originalMethod, object[] __args, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            _fileDepth++;
        }

        private static void FilePostfix(MethodBase __originalMethod, object[] __args, long __state)
        {
            _fileDepth--;
            string path = __args != null && __args.Length > 0 ? __args[0] as string : null;
            if (string.IsNullOrEmpty(path)) return;
            string kind = __originalMethod.DeclaringType == typeof(Script) ? (__originalMethod.Name == "DoFile" ? "dofile" : "компиляция") : "LuaLoader";
            string key = "[Lua " + kind + "] " + Shorten(path);
            Entry e;
            if (!ByKey.TryGetValue(key, out e))
            {
                e = new Entry { Name = key, Category = "LuaFile" };
                ByKey[key] = e;
                ProfilerPatches.All.Add(e);
            }
            long dt = Stopwatch.GetTimestamp() - __state;
            e.Ticks += dt;
            e.FrameTicks += dt;
            e.Calls++;
            if (dt > e.MaxTicks) e.MaxTicks = dt;
            if (LoadingPatches.IsLoading) { e.LoadTicks += dt; e.LoadCalls++; }
        }

        private static string Shorten(string p)
        {
            p = p.Replace('\\', '/');
            int i = p.IndexOf("Info/Lua/", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? p.Substring(i + 9) : p;
        }

        // ---------- отчёт после загрузки ----------
        public static void ResetLoad()
        {
            foreach (var e in ProfilerPatches.All) { e.LoadTicks = 0; e.LoadCalls = 0; }
        }

        public static string LoadReport(int frames)
        {
            double k = 1000.0 / Stopwatch.Frequency;
            var sb = new StringBuilder();
            sb.Append("Что работало во время загрузки (мс всего | вызовов | что). Lua-файлы и этапы — включительно, вложенное считается повторно:\n");
            foreach (var e in ProfilerPatches.All.Where(x => x.LoadTicks > 0).OrderByDescending(x => x.LoadTicks).Take(40))
                sb.AppendFormat("{0,9:0} {1,7}  {2}\n", e.LoadTicks * k, e.LoadCalls, e.Name);
            return sb.ToString();
        }
    }
}
