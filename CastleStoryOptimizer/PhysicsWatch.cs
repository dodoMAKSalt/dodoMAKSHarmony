using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Диагностика оставшихся регулярных фризов в фазе физики (~55 мс раз в ~1.4 с) и стоимости ИИ.
    ///  * колбэки физики OnTrigger*/OnCollision* всех игровых скриптов;
    ///  * все методы игры, которые создают/меняют сеточные коллайдеры (PhysX платит за это на след. шаге);
    ///  * ИИ (Behavior.Tick) — по типам поведения, с числом экземпляров.
    /// </summary>
    internal static class PhysicsWatch
    {
        private static readonly string[] Callbacks = { "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit", "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit" };
        private static readonly Dictionary<Type, Entry> ByType = new Dictionary<Type, Entry>();
        private static readonly Dictionary<Type, HashSet<int>> Instances = new Dictionary<Type, HashSet<int>>();

        public static void Apply(Harmony h)
        {
            int n = 0;
            // 1. Колбэки физики
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (an != "Assembly-CSharp-firstpass" && an != "Brix.Core" && an != "Assembly-CSharp") continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var t in types)
                {
                    if (t == null || t.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    foreach (var cb in Callbacks)
                    {
                        var m = t.GetMethod(cb, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (m == null || m.GetMethodBody() == null) continue;
                        ProfilerPatches.TimeMethod(h, m, "[Колбэк физики] " + t.Name + "." + cb, "Script");
                        n++;
                    }
                }
            }

            // 2. Изменение сеточных коллайдеров
            Time(h, "MegaVoxelGeo", "RefreshCollider", "[Коллайдер] рельеф MegaVoxelGeo.RefreshCollider", ref n);
            Time(h, "MeshAbstrait", "RendreCollider", "[Коллайдер] MeshAbstrait.RendreCollider", ref n);
            Time(h, "Brix.Engine.MegaVoxel+NatureBatchedObject", "UpdateCollider", "[Коллайдер] природа (LOD) UpdateCollider", ref n);
            Time(h, "Brix.Engine.Nature.BoulderRegistry", "CreateDamagedBoulderGameObject", "[Коллайдер] повреждённый валун", ref n);
            Time(h, "Brix.Engine.Nature.MineralRegistry", "CreateDamagedMineralGameObject", "[Коллайдер] повреждённый минерал", ref n);
            Time(h, "Brix.Engine.MaterialCombineHarvester", "ApplyCombine", "[Коллайдер] MaterialCombineHarvester.ApplyCombine", ref n);
            Time(h, "Brix.Engine.MaterialCombineHarvester", "ClearCollider", "[Коллайдер] MaterialCombineHarvester.ClearCollider", ref n);
            Time(h, "Brix.Game.Components.BlueprintRenderer", "RebuildMesh", "[Коллайдер] чертёж BlueprintRenderer.RebuildMesh", ref n);
            Time(h, "Brix.Engine.FlatWorld.ColliderUpdater", "AssignMeshForCollider", "[Коллайдер] рельеф (чанк) AssignMeshForCollider", ref n);

            // 3. ИИ по типам
            var tick = AccessTools.Method(AccessTools.TypeByName("Brix.Game.Components.Behavior"), "Tick");
            if (tick != null)
            {
                try
                {
                    h.Patch(tick, prefix: new HarmonyMethod(typeof(PhysicsWatch), nameof(TickPrefix)),
                                  postfix: new HarmonyMethod(typeof(PhysicsWatch), nameof(TickPostfix)));
                    n++;
                }
                catch (Exception e) { Plugin.Log.LogWarning("ИИ по типам: " + e.Message); }
            }
            Plugin.Log.LogInfo("Диагностика физики и ИИ: перехвачено " + n);
        }

        private static void Time(Harmony h, string type, string method, string name, ref int n)
        {
            var t = AccessTools.TypeByName(type);
            var m = t == null ? null : AccessTools.Method(t, method);
            if (m == null) { Plugin.Log.LogWarning("Не найден " + type + "." + method); return; }
            ProfilerPatches.TimeMethod(h, m, name, "Script");
            n++;
        }

        private static void TickPrefix(out long __state) { ProfilerPatches.MemPush(); __state = Stopwatch.GetTimestamp(); }

        private static void TickPostfix(object __instance, long __state)
        {
            long dt = Stopwatch.GetTimestamp() - __state;
            var t = __instance.GetType();
            Entry e;
            if (!ByType.TryGetValue(t, out e))
            {
                e = new Entry { Name = "[ИИ] " + t.Name, Category = "AI" };
                ByType[t] = e;
                Instances[t] = new HashSet<int>();
                ProfilerPatches.All.Add(e);
            }
            ProfilerPatches.MemPop(e); // мусор по типам ИИ
            e.Ticks += dt;
            e.FrameTicks += dt;
            e.Calls++;
            if (dt > e.MaxTicks) e.MaxTicks = dt;
            if (LoadingPatches.IsLoading) { e.LoadTicks += dt; e.LoadCalls++; }
            var mb = __instance as UnityEngine.Object;
            if (mb != null) Instances[t].Add(mb.GetInstanceID());
        }

        /// <summary>Сколько экземпляров каждого типа ИИ тикало (для отчёта).</summary>
        public static string AiSummary()
        {
            if (Instances.Count == 0) return string.Empty;
            var parts = new List<string>();
            foreach (var kv in Instances) parts.Add(kv.Key.Name + "×" + kv.Value.Count);
            parts.Sort();
            return "\n  ИИ (экземпляров): " + string.Join(", ", parts.ToArray());
        }
    }
}
