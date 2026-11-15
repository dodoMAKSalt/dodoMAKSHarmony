using System;
using System.Collections.Generic;
using System.Linq;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Pathfinding;
using Brix.Game.Rules;
using Brix.Pathfinding;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Исправления ИИ бриктронов.
    ///
    /// 1) Несколько бриктронов несут материал на ОДИН и тот же блок, первый ставит, остальные несут обратно.
    ///    В BuildGoal.IsMatchFor проверка «цель уже занята» есть только для разборки (IsAnti).
    ///    Теперь: простой блок (1 предмет) — максимум 1 рабочий; сложная постройка — не больше, чем не хватает предметов.
    ///
    /// 2) Долго «думают» над выбором работы. Перед КАЖДОЙ попыткой взять задачу бриктрон ждёт в глобальной очереди
    ///    до 8×2+1 = 17 тиков (~0.34 с); если первые задачи не подходят — ожидания складываются в секунды.
    ///    Уменьшаем до 4×1+1 = 5 тиков. Плюс бюджет поиска пути 100 → 250 шагов за тик (пути у нас почти бесплатные).
    /// </summary>
    internal static class AiFixes
    {
        private static System.Reflection.FieldInfo _workers;

        public static void Apply(Harmony h, bool crowding, bool fastDecisions)
        {
            if (crowding)
            {
                try
                {
                    _workers = AccessTools.Field(typeof(Goal), "workers");
                    h.Patch(AccessTools.Method(typeof(BuildGoal), nameof(BuildGoal.IsMatchFor)),
                        postfix: new HarmonyMethod(typeof(AiFixes), nameof(BuildMatchPostfix)));
                    Plugin.Log.LogInfo("ИИ: на один блок теперь несёт материал один бриктрон.");
                }
                catch (Exception e) { Plugin.Log.LogError("ИИ: фикс толкучки не применён: " + e); }
            }
            if (fastDecisions)
            {
                try
                {
                    AccessTools.Field(typeof(Labor), "_tickOffset").SetValue(null, 1);
                    AccessTools.Field(typeof(Labor), "_maxConcurrentBricktrons").SetValue(null, 4);
                    Plugin.Log.LogInfo("ИИ: очередь раздумий бриктронов 17 → 5 тиков.");
                }
                catch (Exception e) { Plugin.Log.LogError("ИИ: ускорение решений не применено: " + e); }
            }
        }

        /// <summary>После загрузки катки (правила из gameplayTest.lua уже применены).</summary>
        public static void OnLevelLoaded(bool fastDecisions)
        {
            if (!fastDecisions) return;
            try
            {
                RaiseStatic("_defaultSearchPathBudget", 250);
                RaiseStatic("_freeAgentSearchPathBudget", 250);
                RaiseStatic("_buildProjectSearchPathBudget", 100);
                Plugin.Log.LogInfo(string.Format("ИИ: бюджет поиска пути {0}/{1}/{2}",
                    GeneralRules.DefaultSearchPathBudget, GeneralRules.FreeAgentSearchPathBudget, GeneralRules.BuildProjectSearchPathBudget));
            }
            catch (Exception e) { Plugin.Log.LogWarning("ИИ: бюджет путей: " + e.Message); }
        }

        private static void RaiseStatic(string field, int min)
        {
            var f = AccessTools.Field(typeof(GeneralRules), field);
            if (f == null) return;
            if ((int)f.GetValue(null) < min) f.SetValue(null, min);
        }

        private static void BuildMatchPostfix(BuildGoal __instance, Labor labor, ref bool __result)
        {
            if (!__result) return;
            try
            {
                if (__instance.IsAnti) return; // для разборки игра уже проверяет сама
                var workers = _workers.GetValue(__instance) as List<Labor>;
                if (workers == null || workers.Count == 0) return;
                int others = workers.Count - (workers.Contains(labor) ? 1 : 0);
                if (others <= 0) return;
                int limit = NeededItems(__instance);
                if (others >= limit) __result = false;
            }
            catch { /* на любой странности — поведение оригинала */ }
        }

        /// <summary>Сколько бриктронов пускать на цель: простой блок (1 предмет) — 1, сложная постройка — до 3.</summary>
        private static int NeededItems(BuildGoal goal)
        {
            var rec = goal.recepteur;
            return rec != null && rec.HasComplexRecipe() ? 3 : 1;
        }
    }

    /// <summary>
    /// 3) Застрявшие бриктроны. Игра замечает только «5 с не двигаюсь на маршруте» → отменяет маршрут,
    ///    берёт следующую задачу, снова не доходит — и так без конца. Яму, из которой нет выхода, она не видит.
    ///
    ///    Раз в 3 с проверяем своих бриктронов: сколько клеток вокруг достижимо (поиск по графу путей).
    ///    Если < 20 клеток 20+ секунд подряд — «застрял»: уведомление на экране.
    ///    Через 30+ секунд, если умеет копать, — приказ выкопать ПРИРОДНЫЙ грунт в сторону открытого места
    ///    (построенные блоки не трогаем). Работает у хоста / в одиночке (ИИ считается там).
    /// </summary>
    internal sealed class StuckWatcher
    {
        private sealed class State { public float TrappedSince = -1; public float LastDig; public int Digs; public bool Notified; }

        private readonly Dictionary<int, State> _states = new Dictionary<int, State>();
        private readonly List<KeyValuePair<float, string>> _toasts = new List<KeyValuePair<float, string>>();
        private float _next;
        private readonly List<Labor> _snapshot = new List<Labor>();
        private static readonly HashSet<Labor> All = new HashSet<Labor>();

        public static void Register(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Labor), "Start"), postfix: new HarmonyMethod(typeof(StuckWatcher), nameof(OnStart)));
            h.Patch(AccessTools.Method(typeof(Labor), "OnDestroy"), prefix: new HarmonyMethod(typeof(StuckWatcher), nameof(OnGone)));
        }
        private static void OnStart(Labor __instance) { All.Add(__instance); }
        private static void OnGone(Labor __instance) { All.Remove(__instance); }
        private readonly bool _autoDig;
        private GUIStyle _style;
        private static readonly XYZ[] Dirs = { new XYZ(1, 0, 0), new XYZ(-1, 0, 0), new XYZ(0, 0, 1), new XYZ(0, 0, -1) };

        public static int TrappedCount;

        public StuckWatcher(bool autoDig) { _autoDig = autoDig; }

        public void Tick()
        {
            if (Brix.Engine.Architecte.instance == null || LoadingPatches.IsLoading) { _states.Clear(); All.RemoveWhere(l => l == null); return; }
            if (Brix.Engine.Neo.IsMultiplayer && !NetworkServer.active) return;
            float now = Time.time;
            if (now < _next) return;
            _next = now + 3f;

            // Список бриктронов ведём сами (Start/OnDestroy): FindObjectsOfType стоил ~47 мс — фриз раз в 15 с.
            _snapshot.Clear();
            _snapshot.AddRange(All);
            int trapped = 0;
            foreach (var labor in _snapshot)
            {
                if (labor == null || !Affiliation.BelongsToMe(labor.gameObject) || labor.presence == null) continue;
                int id = labor.GetInstanceID();
                State st;
                if (!_states.TryGetValue(id, out st)) _states[id] = st = new State();

                if (!IsTrapped(labor)) { st.TrappedSince = -1; st.Digs = 0; st.Notified = false; continue; }
                trapped++;
                if (st.TrappedSince < 0) st.TrappedSince = now;
                float dur = now - st.TrappedSince;

                bool canDig = CanDig(labor);
                if (dur >= 20f && !st.Notified)
                {
                    st.Notified = true;
                    var p = labor.presence.CurrentVoxel;
                    Toast(string.Format("Бриктрон {0} застрял ({1}, {2}, {3}) — {4}", labor.name.Replace("(Clone)", "").Trim(),
                        p.x, p.y, p.z, canDig && _autoDig ? "пробую выкопаться" : "нужна помощь: прокопайте выход"));
                }
                if (_autoDig && canDig && dur >= 30f && now - st.LastDig >= 12f && st.Digs < 8)
                {
                    if (TryDigOut(labor)) { st.LastDig = now; st.Digs++; }
                }
            }
            TrappedCount = trapped;
        }

        private static bool CanDig(Labor labor)
        {
            try
            {
                var prof = labor.Profession;
                return prof != null && (prof.CanMakeWay() || prof.CanBreakVoxels());
            }
            catch { return false; }
        }

        /// <summary>Достижимо меньше 20 клеток — заперт.</summary>
        private static bool IsTrapped(Labor labor)
        {
            try
            {
                var start = labor.presence.CurrentVoxel;
                var perm = labor.navigation != null && labor.navigation.Locomotion != null && labor.navigation.Locomotion.CanClimb
                    ? PathBranch.BranchType.Climbing : PathBranch.BranchType.StepDown;
                var seen = new HashSet<XYZ> { start };
                var queue = new Queue<XYZ>();
                queue.Enqueue(start);
                while (queue.Count > 0 && seen.Count < 20)
                {
                    var v = queue.Dequeue();
                    foreach (var n in PathNodeRegistry.GetPositionInReach(v, perm))
                        if (seen.Add(n)) queue.Enqueue(n);
                }
                return seen.Count < 20;
            }
            catch { return false; } // нет узла путей и т.п. — не считаем застрявшим
        }

        /// <summary>Копаем природный грунт в сторону, где больше пустоты.</summary>
        private static bool TryDigOut(Labor labor)
        {
            var pos = labor.presence.CurrentVoxel;
            XYZ best = pos;
            int bestScore = -1;
            foreach (var d in Dirs)
            {
                foreach (var dy in new[] { 0, 1 })
                {
                    var wall = pos + d + new XYZ(0, dy, 0);
                    if (!Voxel.IsTerrain(wall) || Voxel.IsBlock(wall)) continue; // только природный грунт
                    int score = 0;
                    for (int k = 2; k <= 5; k++)
                        if (Voxel.IsEmpty(pos + new XYZ(d.x * k, dy, d.z * k))) score++;
                    if (score > bestScore) { bestScore = score; best = wall; }
                }
            }
            if (bestScore < 0) return false;
            try
            {
                Order.ForLabor.GoDig.Send(labor, new SpaceLocation(best.ToVector3(), Quaternion.identity), Labor.DecisionSource.User);
                Plugin.Log.LogInfo("Застрявший " + labor.name + " копает выход в " + best);
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Не удалось отдать приказ копать: " + e.Message); return false; }
        }

        private void Toast(string text)
        {
            _toasts.Add(new KeyValuePair<float, string>(Time.realtimeSinceStartup + 10f, text));
            Plugin.Log.LogInfo(text);
        }

        public void DrawGUI()
        {
            if (_toasts.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            _toasts.RemoveAll(t => t.Key < now);
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 16 };
            for (int i = 0; i < _toasts.Count; i++)
            {
                var r = new Rect(0, Screen.height * 0.18f + i * 26, Screen.width, 26);
                _style.normal.textColor = new Color(0, 0, 0, 0.85f);
                GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), "⚠ " + _toasts[i].Value, _style);
                _style.normal.textColor = new Color(1f, 0.55f, 0.2f);
                GUI.Label(r, "⚠ " + _toasts[i].Value, _style);
            }
        }
    }
}
