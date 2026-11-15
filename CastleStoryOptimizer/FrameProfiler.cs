using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Собирает статистику окнами по 1 секунде, рисует оверлей и пишет отчёты.
    /// </summary>
    internal sealed class FrameProfiler
    {
        private const int TopN = 18;
        private const float SpikeMs = 100f;

        private struct Row
        {
            public string Name;
            public string Category;
            public double MsPerFrame;
            public double CallsPerFrame;
            public double MaxMs;
            public double AllocKbPerSec;
        }

        private sealed class Snapshot
        {
            public DateTime Time;
            public int Frames;
            public double AvgFrameMs;
            public double MaxFrameMs;
            public int Spikes;
            public int Gc0;
            public long HeapMb;
            public double HeapGrowthKbPerSec = -1; // -1: в окне была сборка мусора
            public List<Row> Rows;
            public string Text;
        }

        public bool ShowOverlay = true;

        private readonly string _reportPath;
        private readonly int _autoReportSeconds;

        private float _windowStart;
        private int _frames;
        private double _frameMsSum;
        private double _frameMsMax;
        private int _spikes;
        private int _gcAtWindowStart;
        private long _memAtWindowStart;
        private readonly Dictionary<string, long> _allocAcc = new Dictionary<string, long>();
        private double _accSecs, _accGrowthSecs;
        private long _accGrowth;

        /// <summary>Кто создаёт мусор — накоплено с прошлого отчёта (одна секунда слишком шумная).</summary>
        private string GarbageReport()
        {
            if (_accSecs <= 0) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendFormat("Мусор за {0:0} с: куча растёт в среднем на {1} КБ/с (между сборками). Кто создаёт, КБ/с\n" +
                "(включительно: вложенное считается и у родителя, напр. GameClock.FixedUpdate включает весь ИИ; точность — блоками ~4 КБ):\n",
                _accSecs, _accGrowthSecs > 0 ? (_accGrowth / 1024.0 / _accGrowthSecs).ToString("0") : "?");
            foreach (var kv in _allocAcc.OrderByDescending(k => k.Value).Take(30))
            {
                double kbs = kv.Value / 1024.0 / _accSecs;
                if (kbs < 0.5) break;
                sb.AppendFormat("{0,8:0.0}  {1}\n", kbs, kv.Key);
            }
            _allocAcc.Clear(); _accSecs = 0; _accGrowth = 0; _accGrowthSecs = 0;
            return sb.ToString();
        }

        private Snapshot _last;
        private Snapshot _worst;
        private float _lastAutoReport;

        private GUIStyle _style;
        private readonly double _tickToMs = 1000.0 / Stopwatch.Frequency;

        // --- разбор отдельных фризов ---
        private const float SpikeCaptureMs = 50f;
        private readonly string _spikesPath;
        private readonly string _timelinePath;
        private int _lastGcCount;
        private int _spikeCount;
        private bool _windowHadLoading;

        public FrameProfiler(string bepinexRoot, int autoReportSeconds)
        {
            _reportPath = Path.Combine(bepinexRoot, "CastleStoryOptimizer_report.txt");
            _spikesPath = Path.Combine(bepinexRoot, "CastleStoryOptimizer_spikes.txt");
            _timelinePath = Path.Combine(bepinexRoot, "CastleStoryOptimizer_timeline.csv");
            _autoReportSeconds = autoReportSeconds;
            try
            {
                File.WriteAllText(_spikesPath, "Кадры дольше " + SpikeCaptureMs + " мс (вне загрузки). Время | длина кадра | GC в этом кадре | куча | что было тяжёлым в этом кадре" + Environment.NewLine);
                File.WriteAllText(_timelinePath, "time,fps,avg_ms,max_ms,spikes100,gc_per_s,heap_mb,path_ms,lua_ms,loading" + Environment.NewLine);
            }
            catch { }
            _lastGcCount = GC.CollectionCount(0);
            try
            {
                File.WriteAllText(_reportPath, "Castle Story Optimizer — отчёт профайлера, запуск " + DateTime.Now + Environment.NewLine +
                    "Колонки: мс/кадр (среднее за окно 1 с) | вызовов/кадр | макс. один вызов, мс" + Environment.NewLine +
                    "Категории могут вкладываться (Lua внутри Update и т.п.), поэтому сумма > времени кадра — это нормально." + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Не могу создать отчёт: " + e.Message); }
            _windowStart = Time.realtimeSinceStartup;
            _gcAtWindowStart = GC.CollectionCount(0);
        }

        public void Tick()
        {
            ProfilerPatches.ResetDepthGuards();

            float dtMs = Time.unscaledDeltaTime * 1000f;
            _frames++;
            _frameMsSum += dtMs;
            if (dtMs > _frameMsMax) _frameMsMax = dtMs;
            if (dtMs > SpikeMs) _spikes++;

            bool loading = LoadingPatches.IsLoading;
            if (loading) _windowHadLoading = true;
            int gcNow = GC.CollectionCount(0);
            if (dtMs > SpikeCaptureMs && !loading && Time.frameCount > 10)
                CaptureSpike(dtMs, gcNow != _lastGcCount);
            _lastGcCount = gcNow;
            // Запоминаем топ-4 прошлого кадра (без строк — чтобы не мусорить): нужно для фризов физики,
            // за изменения коллайдеров PhysX «платит» на следующем шаге.
            for (int i = 0; i < 4; i++) { _prevE[i] = null; _prevV[i] = 0; }
            foreach (var e in ProfilerPatches.All)
            {
                long v = e.FrameTicks;
                if (v != 0 && e.Category != "Frame" && e.Category != "Stage" && e.Category != "Render")
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (v <= _prevV[i]) continue;
                        for (int j = 3; j > i; j--) { _prevV[j] = _prevV[j - 1]; _prevE[j] = _prevE[j - 1]; }
                        _prevV[i] = v; _prevE[i] = e;
                        break;
                    }
                }
                e.FrameTicks = 0;
            }

            float now = Time.realtimeSinceStartup;
            if (now - _windowStart < 1f) return;

            _last = BuildSnapshot();
            AppendTimeline(_last, _windowHadLoading);
            // Окна с загрузкой и с <10 кадрами (экраны ожидания) в «худшее» не берём.
            if (!_windowHadLoading && _last.Frames >= 10 && _last.MaxFrameMs < 5000 &&
                (_worst == null || _last.AvgFrameMs > _worst.AvgFrameMs))
                _worst = _last;
            _windowHadLoading = false;

            _windowStart = now;
            _frames = 0;
            _frameMsSum = 0;
            _frameMsMax = 0;
            _spikes = 0;
            _gcAtWindowStart = GC.CollectionCount(0);
            _memAtWindowStart = GC.GetTotalMemory(false);

            if (_autoReportSeconds > 0 && now - _lastAutoReport >= _autoReportSeconds)
            {
                _lastAutoReport = now;
                WriteReport("авто");
            }
        }

        private Snapshot BuildSnapshot()
        {
            int frames = Math.Max(1, _frames);
            double secs = Time.realtimeSinceStartup - _windowStart;
            int gcs = GC.CollectionCount(0) - _gcAtWindowStart;
            long memNow = GC.GetTotalMemory(false);
            var rows = new List<Row>(ProfilerPatches.All.Count);
            foreach (var e in ProfilerPatches.All)
            {
                long ticks = Interlocked.Exchange(ref e.Ticks, 0);
                long calls = Interlocked.Exchange(ref e.Calls, 0);
                long max = e.MaxTicks; e.MaxTicks = 0;
                long alloc = e.AllocBytes; e.AllocBytes = 0;
                if (alloc > 0)
                {
                    long acc; _allocAcc.TryGetValue(e.Name, out acc); _allocAcc[e.Name] = acc + alloc;
                }
                if (calls == 0) continue;
                rows.Add(new Row
                {
                    Name = e.Name,
                    Category = e.Category,
                    MsPerFrame = ticks * _tickToMs / frames,
                    CallsPerFrame = (double)calls / frames,
                    MaxMs = max * _tickToMs,
                    AllocKbPerSec = alloc / 1024.0 / Math.Max(0.5, secs)
                });
            }
            rows.Sort((a, b) => b.MsPerFrame.CompareTo(a.MsPerFrame));

            _accSecs += secs;
            if (gcs == 0 && _memAtWindowStart > 0) { _accGrowth += Math.Max(0, memNow - _memAtWindowStart); _accGrowthSecs += secs; }
            var s = new Snapshot
            {
                Time = DateTime.Now,
                Frames = _frames,
                AvgFrameMs = _frameMsSum / frames,
                MaxFrameMs = _frameMsMax,
                Spikes = _spikes,
                Gc0 = GC.CollectionCount(0) - _gcAtWindowStart,
                HeapMb = memNow / (1024 * 1024),
                HeapGrowthKbPerSec = gcs == 0 && _memAtWindowStart > 0 ? Math.Max(0, memNow - _memAtWindowStart) / 1024.0 / Math.Max(0.5, secs) : -1,
                Rows = rows
            };
            s.Text = Format(s, TopN);
            return s;
        }

        private static string Format(Snapshot s, int top)
        {
            var sb = new StringBuilder();
            double fps = s.AvgFrameMs > 0 ? 1000.0 / s.AvgFrameMs : 0;
            sb.AppendFormat("FPS {0:0}  кадр {1:0.0} мс (макс {2:0}),  фризов>{3:0}мс: {4},  GC: {5}/с,  куча {6} МБ\n",
                fps, s.AvgFrameMs, s.MaxFrameMs, SpikeMs, s.Spikes, s.Gc0, s.HeapMb);

            double lua = s.Rows.Where(r => r.Category == "Lua").Sum(r => r.MsPerFrame);
            double path = s.Rows.Where(r => r.Category == "Path").Sum(r => r.MsPerFrame);
            double render = s.Rows.Where(r => r.Category == "Render").Sum(r => r.MsPerFrame);
            double scripts = s.Rows.Where(r => r.Category != "Render" && r.Category != "Lua" && r.Category != "Path" && r.Category != "Frame"
                && r.Category != "Stage" && r.Category != "UI" && r.Category != "Clock" && r.Category != "Coroutine" && r.Category != "LuaFile" && r.Category != "AI").Sum(r => r.MsPerFrame);
            double clock = s.Rows.Where(r => r.Category == "Clock").Sum(r => r.MsPerFrame);
            double coro = s.Rows.Where(r => r.Category == "Coroutine").Sum(r => r.MsPerFrame);
            double busy = s.Rows.Where(r => r.Name.StartsWith("[Кадр] процессор")).Sum(r => r.MsPerFrame);
            double wait = s.Rows.Where(r => r.Name.StartsWith("[Кадр] ожидание")).Sum(r => r.MsPerFrame);
            if (busy > 0)
                sb.AppendFormat("Кадр: ПРОЦЕССОР {0:0.0} мс | ожидание GPU/лимита {1:0.0} мс   (для 120 FPS нужно ≤ 8.3)\n", busy, wait);
            sb.AppendFormat("Итого: Рендер {0:0.0}  Скрипты {1:0.0}  Пути {2:0.00}  Lua {3:0.00}  прочее (физика/корутины/анимации) ≈ {4:0.0} мс\n",
                render, scripts, path, lua, Math.Max(0, (busy > 0 ? busy : s.AvgFrameMs) - render - scripts));
            if (clock > 0 || coro > 0)
                sb.AppendFormat("  подписчики GameClock (внутри «Скриптов») {0:0.0} мс   корутины ≈ {1:0.0} мс (вложенные могут считаться дважды)\n", clock, coro);
            if (!string.IsNullOrEmpty(SceneStats)) sb.Append("  сцена: ").Append(SceneStats).Append('\n');
            sb.Append("--------------------------------------------------------------\n");
            foreach (var r in s.Rows.Take(top))
                sb.AppendFormat("{0,7:0.00}  {1,6:0.#}x  {2,6:0.0}  {3}\n", r.MsPerFrame, r.CallsPerFrame, r.MaxMs, r.Name);
            return sb.ToString();
        }

        // ---------- статистика сцены ----------
        private static string SceneStats;

        /// <summary>Сколько в сцене аниматоров, частиц, анимированных мешей и сколько из них видно.
        /// full = ещё и все рендереры (дорого, только по F9).</summary>
        private void CollectSceneStats(bool full)
        {
            try
            {
                int anim = 0, animOn = 0, animVisible = 0;
                foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>())
                {
                    anim++;
                    if (!a.isActiveAndEnabled) continue;
                    animOn++;
                    var r = a.GetComponentInChildren<Renderer>();
                    if (r != null && r.isVisible) animVisible++;
                }
                int skinned = 0, skinnedVis = 0;
                foreach (var r in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>()) { skinned++; if (r.isVisible) skinnedVis++; }
                int ps = 0, psPlaying = 0;
                foreach (var p in UnityEngine.Object.FindObjectsOfType<ParticleSystem>()) { ps++; if (p.isPlaying) psPlaying++; }
                int rb = 0, rbAwake = 0;
                foreach (var b in UnityEngine.Object.FindObjectsOfType<Rigidbody>()) { rb++; if (!b.isKinematic && !b.IsSleeping()) rbAwake++; }
                var sb = new StringBuilder();
                sb.AppendFormat("аниматоров {0} (вкл {1}, видно {2}), скин-мешей {3} (видно {4}), частиц {5} (играет {6}), тел физики {7} (активных {8})",
                    anim, animOn, animVisible, skinned, skinnedVis, ps, psPlaying, rb, rbAwake);
                if (full)
                {
                    int rend = 0, rendVis = 0;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>()) { rend++; if (r.isVisible) rendVis++; }
                    int go = UnityEngine.Object.FindObjectsOfType<Transform>().Length;
                    sb.AppendFormat(", рендереров {0} (видно {1}), объектов {2}", rend, rendVis, go);
                    sb.Append("\n  физика: ").Append(PhysicsPatches.Stats());
                }
                sb.Append(ExtraStats()).Append(PhysicsWatch.AiSummary());
                if (!string.IsNullOrEmpty(MovingColliderFix.LastReport)) sb.Append("\n  ").Append(MovingColliderFix.LastReport);
                SceneStats = sb.ToString();
            }
            catch (Exception e) { SceneStats = "ошибка: " + e.Message; }
        }

        private long _lastNetBytes;
        private float _lastNetTime;

        /// <summary>Память Unity/Mono, сеть, бриктроны и задачи.</summary>
        private string ExtraStats()
        {
            var sb = new StringBuilder();
            try
            {
                const float mb = 1024f * 1024f;
                sb.AppendFormat("\n  память: Unity выделено {0:0} МБ / зарезервировано {1:0} МБ, Mono занято {2:0} МБ / куча {3:0} МБ, сборок мусора всего {4}",
                    UnityEngine.Profiling.Profiler.GetTotalAllocatedMemory() / mb,
                    UnityEngine.Profiling.Profiler.GetTotalReservedMemory() / mb,
                    UnityEngine.Profiling.Profiler.GetMonoUsedSize() / mb,
                    UnityEngine.Profiling.Profiler.GetMonoHeapSize() / mb,
                    GC.CollectionCount(0));
            }
            catch (Exception e) { sb.Append("\n  память: ошибка ").Append(e.Message); }

            try
            {
                int labors = UnityEngine.Object.FindObjectsOfType<Brix.Game.AI.Labor>().Length;
                int projects = UnityEngine.Object.FindObjectsOfType<Brix.Game.AI.Project>().Length;
                sb.AppendFormat("\n  юнитов с ИИ {0}, задач {1}, частота физики {2:0} Гц", labors, projects, 1f / Time.fixedDeltaTime);
            }
            catch (Exception e) { sb.Append("\n  юниты: ошибка ").Append(e.Message); }

            try
            {
                if (UnityEngine.Networking.NetworkServer.active || UnityEngine.Networking.NetworkClient.active)
                {
                    long bytes = UnityEngine.Networking.NetworkTransport.GetOutgoingFullBytesCount();
                    float now = Time.realtimeSinceStartup;
                    float kbps = _lastNetTime > 0 ? (bytes - _lastNetBytes) / 1024f / Mathf.Max(0.1f, now - _lastNetTime) : 0;
                    _lastNetBytes = bytes; _lastNetTime = now;
                    sb.AppendFormat("\n  сеть: {0}, исходящих {1:0.0} КБ/с, подключений {2}",
                        UnityEngine.Networking.NetworkServer.active ? "хост" : "клиент", kbps,
                        UnityEngine.Networking.NetworkServer.active ? UnityEngine.Networking.NetworkServer.connections.Count : 1);
                }
            }
            catch (Exception e) { sb.Append("\n  сеть: ошибка ").Append(e.Message); }
            return sb.ToString();
        }


        public void WriteReport(string reason)
        {
            if (_last == null) return;
            // Статистика сцены перебирает все объекты (~100+ мс) — только по F9, не в авто-отчётах.
            if (reason.Contains("F9") && Brix.Engine.Architecte.instance != null) CollectSceneStats(true);
            try
            {
                var sb = new StringBuilder();
                sb.AppendFormat("===== {0:HH:mm:ss} — {1} =====\n", DateTime.Now, reason);
                sb.Append("Последняя секунда:\n").Append(Format(_last, 30));
                if (_worst != null)
                    sb.AppendFormat("\nХудшая секунда за сессию ({0:HH:mm:ss}):\n", _worst.Time).Append(Format(_worst, 30));
                sb.Append('\n').Append(GarbageReport());
                sb.Append('\n');
                File.AppendAllText(_reportPath, sb.ToString().Replace("\n", Environment.NewLine));
                if (reason != "авто") Plugin.Log.LogInfo("Отчёт записан: " + _reportPath);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Не удалось записать отчёт: " + e.Message); }
        }

        private readonly Entry[] _prevE = new Entry[4];
        private readonly long[] _prevV = new long[4];

        private void CaptureSpike(float dtMs, bool gc)
        {
            if (++_spikeCount > 2000) return; // защита от разрастания файла
            try
            {
                double covered = 0;
                var top = new List<KeyValuePair<double, string>>();
                foreach (var e in ProfilerPatches.All)
                {
                    if (e.FrameTicks == 0) continue;
                    double ms = e.FrameTicks * _tickToMs;
                    if (e.Category != "Lua" && e.Category != "Path" && e.Category != "Frame" && e.Category != "Stage") covered += ms; // Lua/пути вложены в Update — не суммируем дважды
                    top.Add(new KeyValuePair<double, string>(ms, e.Name));
                }
                top.Sort((a, b) => b.Key.CompareTo(a.Key));
                var sb = new StringBuilder();
                sb.AppendFormat("{0:HH:mm:ss}  {1,6:0} мс  {2}  куча {3} МБ  | замерено {4:0} мс | ",
                    DateTime.Now, dtMs, gc ? "GC!" : "   ", GC.GetTotalMemory(false) / (1024 * 1024), covered);
                for (int i = 0; i < top.Count && i < 6; i++)
                    sb.AppendFormat("{0} {1:0.0}; ", top[i].Value, top[i].Key);
                sb.Append(" || прошлый кадр: ");
                for (int i = 0; i < 4 && _prevE[i] != null; i++)
                    sb.AppendFormat("{0} {1:0.0}; ", _prevE[i].Name, _prevV[i] * _tickToMs);
                File.AppendAllText(_spikesPath, sb.ToString() + Environment.NewLine);
            }
            catch { }
        }

        private void AppendTimeline(Snapshot s, bool loading)
        {
            try
            {
                double lua = 0, path = 0;
                foreach (var r in s.Rows)
                {
                    if (r.Category == "Lua") lua += r.MsPerFrame;
                    else if (r.Category == "Path") path += r.MsPerFrame;
                }
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                File.AppendAllText(_timelinePath, string.Format(ci, "{0:HH:mm:ss},{1:0},{2:0.0},{3:0},{4},{5},{6},{7:0.00},{8:0.00},{9}",
                    s.Time, s.AvgFrameMs > 0 ? 1000 / s.AvgFrameMs : 0, s.AvgFrameMs, s.MaxFrameMs, s.Spikes, s.Gc0, s.HeapMb, path, lua, loading ? 1 : 0) + Environment.NewLine);
            }
            catch { }
        }

        public void AppendRaw(string text)
        {
            try { File.AppendAllText(_reportPath, "===== " + DateTime.Now.ToString("HH:mm:ss") + " =====" + Environment.NewLine + text.Replace("\n", Environment.NewLine) + Environment.NewLine); }
            catch (Exception e) { Plugin.Log.LogWarning("Не удалось записать отчёт: " + e.Message); }
        }

        private Snapshot _overlayFor;
        private string _overlayText;

        public void DrawOverlay()
        {
            if (!ShowOverlay || _last == null) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false, richText = false };
                _style.normal.textColor = Color.white;
                var font = Font.CreateDynamicFontFromOSFont("Consolas", 12);
                if (font != null) _style.font = font;
            }
            var rect = new Rect(8, 46, 560, 60 + 15 * Math.Min(TopN, _last.Rows.Count));
            GUI.color = new Color(0, 0, 0, 0.75f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            // Строку собираем раз в секунду, а не дважды за кадр (было ~0.7 МБ мусора в секунду → частые сборки мусора).
            if (!ReferenceEquals(_overlayFor, _last)) { _overlayFor = _last; _overlayText = _last.Text + "F8 — скрыть, F9 — отчёт, F7 — подробный замер: " + (ProfilerPatches.Active ? "ВКЛ" : "ВЫКЛ"); }
            GUI.Label(new Rect(rect.x + 6, rect.y + 4, rect.width - 12, rect.height - 8), _overlayText, _style);
        }
    }
}
