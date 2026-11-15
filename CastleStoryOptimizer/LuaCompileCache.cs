using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Главный тормоз загрузки на ~71%: интерфейс собирается из Lua-панелей через dofile,
    /// и каждый dofile заново КОМПИЛИРУЕТ файл (ButtonPanel.lua ~200 раз — ~10 с, всего ~25 с).
    ///
    /// v0.9.0 кэшировал БАЙТКОД (Dump/LoadStream) — в этой версии MoonSharp undump ломает адреса
    /// (ArgumentOutOfRange в Processing_Loop), меню падало, загрузка не завершалась. Убрано.
    ///
    /// Теперь: если ТОТ ЖЕ Script грузит ТОТ ЖЕ файл с ТЕМ ЖЕ окружением — отдаём уже скомпилированную
    /// функцию-чанк этого скрипта. По семантике Lua dofile(f) ≡ loadfile(f)(): чанк — обычная функция,
    /// её повторный вызов создаёт новые локальные переменные, как и свежая компиляция.
    /// </summary>
    internal static class LuaCompileCache
    {
        private sealed class Item { public Table Env; public DynValue Func; }

        // Script → (путь → чанк). Чистим при каждой загрузке катки и выходе в меню.
        private static readonly Dictionary<Script, Dictionary<string, List<Item>>> Cache = new Dictionary<Script, Dictionary<string, List<Item>>>();
        private static int _hits, _misses;
        private static long _savedTicks;
        private static readonly Dictionary<string, long> CompileTicks = new Dictionary<string, long>();

        public static void Apply(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(Script), nameof(Script.LoadFile), new[] { typeof(string), typeof(Table), typeof(string) });
                h.Patch(m, prefix: new HarmonyMethod(typeof(LuaCompileCache), nameof(Prefix)) { priority = Priority.First },
                           postfix: new HarmonyMethod(typeof(LuaCompileCache), nameof(Postfix)) { priority = Priority.Last });
                Plugin.Log.LogInfo("Кэш Lua-чанков включён (без байткода).");
            }
            catch (Exception e) { Plugin.Log.LogError("Кэш Lua не включён: " + e); }
        }

        public static void Clear()
        {
            Cache.Clear();
        }

        private struct State { public string Key; public Table Env; public long Start; public bool Hit; }

        private static bool Prefix(Script __instance, string filename, Table globalContext, ref DynValue __result, out State __state)
        {
            __state = new State { Start = Stopwatch.GetTimestamp() };
            try
            {
                var env = globalContext ?? __instance.Globals;
                string key = __instance.Options.ScriptLoader.ResolveFileName(filename, env);
                __state.Key = key;
                __state.Env = env;
                Dictionary<string, List<Item>> perScript;
                List<Item> items;
                if (key != null && Cache.TryGetValue(__instance, out perScript) && perScript.TryGetValue(key, out items))
                {
                    foreach (var it in items)
                    {
                        if (!ReferenceEquals(it.Env, env)) continue;
                        __result = it.Func;
                        __state.Hit = true;
                        _hits++;
                        long ct;
                        if (CompileTicks.TryGetValue(key, out ct)) _savedTicks += ct;
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Кэш Lua: " + filename + ": " + e.Message);
            }
            return true;
        }

        private static void Postfix(Script __instance, DynValue __result, State __state)
        {
            if (__state.Hit || __state.Key == null || ReferenceEquals(__result, null) || __result.Type != DataType.Function) return;
            _misses++;
            CompileTicks[__state.Key] = Stopwatch.GetTimestamp() - __state.Start;
            Dictionary<string, List<Item>> perScript;
            if (!Cache.TryGetValue(__instance, out perScript)) Cache[__instance] = perScript = new Dictionary<string, List<Item>>();
            List<Item> items;
            if (!perScript.TryGetValue(__state.Key, out items)) perScript[__state.Key] = items = new List<Item>(1);
            items.Add(new Item { Env = __state.Env, Func = __result });
        }

        public static string Summary()
        {
            return string.Format("Кэш Lua: скриптов {0}, из кэша {1} раз, скомпилировано {2}, сэкономлено ≈ {3:0.0} с",
                Cache.Count, _hits, _misses, _savedTicks / (double)Stopwatch.Frequency);
        }
    }
}
