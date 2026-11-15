using System;
using System.Text;
using Brix.Input;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// 1) CameraDistanceColliderScaler: игра 50 раз в секунду переписывает радиус SphereCollider
    ///    (под расстояние до камеры), даже если он не изменился. Для PhysX каждое присваивание —
    ///    «коллайдер изменился», статичный коллайдер переносится в структурах физики.
    ///    Меняем радиус, только если он реально изменился больше чем на 1%.
    /// 2) Диагностика физики для отчёта (по F9).
    /// </summary>
    internal static class PhysicsPatches
    {
        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(CameraDistanceColliderScaler), "BrixFixedUpdate"),
                    prefix: new HarmonyMethod(typeof(PhysicsPatches), nameof(ScalerPrefix)));
                Plugin.Log.LogInfo(string.Format("Физика: шаг {0:0.000} с, макс. догон {1:0.00} с, итераций солвера {2}. Фикс масштабирования коллайдеров включён.",
                    Time.fixedDeltaTime, Time.maximumDeltaTime, Physics.defaultSolverIterations));
            }
            catch (Exception e) { Plugin.Log.LogError("Фикс коллайдеров не применён: " + e); }
        }

        private static bool ScalerPrefix(CameraDistanceColliderScaler __instance)
        {
            var sphere = __instance.Sphere;
            var cam = Camera.main;
            if (sphere == null || cam == null) return false;
            var lim = __instance.DistanceLimits;
            float num = Vector3.Distance(cam.transform.position, __instance.transform.position) - lim.x;
            float radius = Mathf.Lerp(__instance.SmallScale, __instance.BigScale, num / (lim.y - lim.x));
            float cur = sphere.radius;
            if (Mathf.Abs(radius - cur) > Mathf.Max(0.01f, cur * 0.01f)) sphere.radius = radius;
            return false;
        }

        /// <summary>Сводка по физике сцены (дорого — только по F9).</summary>
        public static string Stats()
        {
            try
            {
                int total = 0, staticCol = 0, trig = 0, mesh = 0, meshStatic = 0, sphere = 0, box = 0, capsule = 0;
                long meshVerts = 0;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
                {
                    if (!c.enabled) continue;
                    total++;
                    bool isStatic = c.attachedRigidbody == null;
                    if (isStatic) staticCol++;
                    if (c.isTrigger) trig++;
                    var mc = c as MeshCollider;
                    if (mc != null)
                    {
                        mesh++;
                        if (isStatic) meshStatic++;
                        if (mc.sharedMesh != null) meshVerts += mc.sharedMesh.vertexCount;
                    }
                    else if (c is SphereCollider) sphere++;
                    else if (c is BoxCollider) box++;
                    else if (c is CapsuleCollider) capsule++;
                }
                int animPhys = 0;
                foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>())
                    if (a.isActiveAndEnabled && a.updateMode == AnimatorUpdateMode.AnimatePhysics) animPhys++;
                var sb = new StringBuilder();
                sb.AppendFormat("коллайдеров {0} (статичных {1}, триггеров {2}; сеточных {3}, из них статичных {4}, вершин {5}; сфер {6}, коробок {7}, капсул {8}); аниматоров «по физике» {9}; шаг {10:0.000} с",
                    total, staticCol, trig, mesh, meshStatic, meshVerts, sphere, box, capsule, animPhys, Time.fixedDeltaTime);
                return sb.ToString();
            }
            catch (Exception e) { return "ошибка: " + e.Message; }
        }
    }
}
