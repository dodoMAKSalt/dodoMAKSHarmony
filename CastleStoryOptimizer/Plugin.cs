using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    [BepInDependency(BtcCompat.Guid, BepInDependency.DependencyFlags.SoftDependency)] // грузиться после BetterThanCastle, если он есть
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "castlestory.optimizer";
        public const string Name = "dodoMAKSharmony";
        public const string Version = "0.18.6";

        internal static Plugin Instance;
        internal static BepInEx.Logging.ManualLogSource Log;

        private ConfigEntry<bool> _disableAnalytics;
        private ConfigEntry<bool> _enableProfiler;
        private ConfigEntry<KeyCode> _overlayKey;
        private ConfigEntry<KeyCode> _dumpKey;
        private ConfigEntry<int> _autoReportSeconds;

        private Harmony _harmony;
        private FrameProfiler _profiler;

        private void Awake()
        {
            Instance = this;
            useGUILayout = false; // OnGUI мода без GUILayout: Unity не делает лишний проход Layout и меньше мусорит
            Log = Logger;

            _disableAnalytics = Config.Bind("Fixes", "DisableAnalytics", true,
                "Отключить GameAnalytics (сервер мёртв, игра впустую шлёт запросы и спамит лог).");
            _enableProfiler = Config.Bind("Profiler", "Enabled", true,
                "Замерять время Update-методов, поиска пути и Lua.");
            _overlayKey = Config.Bind("Profiler", "OverlayKey", KeyCode.F8, "Показать/скрыть оверлей.");
            _dumpKey = Config.Bind("Profiler", "DumpKey", KeyCode.F9, "Записать отчёт в файл.");
            _autoReportSeconds = Config.Bind("Profiler", "AutoReportSeconds", 60,
                "Каждые N секунд дописывать сводку в отчёт (0 = выкл).");

            _harmony = new Harmony(Guid);
            PortCompat.Detect();

            if (_disableAnalytics.Value)
                AnalyticsPatches.Apply(_harmony);

            BtcCompat.Detect();
            var fastLoading = Config.Bind("Fixes", "FastLoading", true,
                "Снимать лимит FPS и vsync на время загрузки мира (каждый шаг загрузки ждёт кадр).");
            LoadingPatches.CollectGarbageAfterLoad = Config.Bind("Fixes", "CollectGarbageAfterLoad", true,
                "Убрать мусор в конце загрузки катки (на экране загрузки), чтобы первый фриз сборщика в игре был намного позже.").Value;
            if (BtcCompat.Present) LoadingPatches.CollectGarbageAfterLoad = false; // их мод сам убирает мусор в конце загрузки
            if (PortCompat.Active) LoadingPatches.CollectGarbageAfterLoad = false;
            LoadingPatches.Apply(_harmony, PortCompat.On(fastLoading.Value, false));

            var chunkMul = Config.Bind("Fixes", "LoadChunkMultiplier", 10,
                "Во сколько раз крупнее порции работы при загрузке катки (1 = как в оригинале, макс. 100).");
            var fastWaits = Config.Bind("Fixes", "FastLoadWaits", true,
                "Убрать паузы по 1 с и искусственное «догоняние» полосы прогресса при загрузке.");
            // опрос «раз в секунду» в загрузчике BetterThanCastle заменяет умнее (с учётом ожидания хоста в мультиплеере)
            if (!PortCompat.Active) FastLoadPatches.Apply(_harmony, chunkMul.Value, fastWaits.Value && !BtcCompat.Present);

            if (!PortCompat.Active && Config.Bind("Fixes", "FixHostFreeze", true,
                    "Убрать фриз ~11 с при создании хоста/подключении (мёртвый DNS NAT-фасилитатора).").Value)
                NetworkPatches.Apply(_harmony);

            var forceRu = Config.Bind("Language", "ForceRussian", true,
                "Всегда включать русский перевод dodoMAKSharmony при запуске (false — язык из настроек игры).");
            RussianLanguage.Apply(_harmony, forceRu.Value);
            if (PortCompat.Csp && Config.Bind("Language", "TranslateCastleStoryPlus", true,
                    "Переводить на русский мод Castle Story Plus (их меню, подсказки, настройки, новые предметы).").Value)
                CspTranslator.Apply(_harmony);

            _stationPriorities = Config.Bind("Workshops", "Enabled", true,
                "Приоритеты мастерских: выделить печь/верстак и т.п. → клавиши ниже.").Value;
            if (_stationPriorities)
                StationPriorities.Apply(_harmony,
                    BtcKey(Config.Bind("Workshops", "PriorityUpKey", KeyCode.PageUp, "Поднять приоритет выделенной мастерской.").Value),
                    BtcKey(Config.Bind("Workshops", "PriorityDownKey", KeyCode.PageDown, "Опустить приоритет выделенной мастерской.").Value),
                    Config.Bind("Workshops", "ShowLabels", true, "Подписи приоритета над мастерскими.").Value);
            if (_stationPriorities && Config.Bind("Workshops", "PieMenuButton", true,
                    "Кнопка «Приоритет» в круговом меню мастерской (ПКМ).").Value)
                PieMenuPriority.Apply(_harmony);

            if (!PortCompat.Active && Config.Bind("Menu", "ShowModName", true, "Надпись dodoMAKSharmony под логотипом в главном меню.").Value)
                _branding = new MenuBranding();

            if (!PortCompat.Active && Config.Bind("Performance", "CullHiddenAnimators", true,
                    "Не обновлять положения у анимаций реквизита за кадром (двери, мастерские и т.п.). Логика анимаций работает.").Value)
                _animCulling = new AnimatorCulling();

            if (!PortCompat.Active && Config.Bind("Fixes", "LuaChunkCache", true,
                    "Компилировать каждый Lua-файл один раз (игра перекомпилирует ButtonPanel.lua ~200 раз за загрузку).").Value)
                LuaCompileCache.Apply(_harmony);

            LoadingDiagnostics.Apply(_harmony, !PortCompat.Active &&
                Config.Bind("Fixes", "FastLuaMenus", true,
                    "Строить Lua-меню несколькими шагами за кадр (ускоряет загрузку на ~71%).").Value,
                _enableProfiler.Value);

            var terrainLinger = Config.Bind("Performance", "TerrainColliderLingerSeconds", 20f,
                "Сколько секунд держать коллайдер рельефа после ухода юнитов (в игре 1 — из-за этого фризы). 1 = как в оригинале.");
            if (terrainLinger.Value > 1f) TerrainColliderPatches.Apply(_harmony, terrainLinger.Value);

            AiFastDecisions = Config.Bind("AI", "FastDecisions", true,
                "Бриктроны быстрее выбирают работу (очередь раздумий 17 → 5 тиков, бюджет поиска пути 100 → 250).").Value;
            if (!PortCompat.Active) AiFixes.Apply(_harmony,
                Config.Bind("AI", "OneCarrierPerBlock", true,
                    "На один блок стройки несёт материал один бриктрон (а не несколько, которые потом несут обратно).").Value,
                AiFastDecisions);

            GcControl.Enabled = !PortCompat.Active && Config.Bind("Performance", "PauseGcWhileLoading", true,
                "Не запускать сборщик мусора во время загрузки мира (у нас было ~10 сборок по 1 с подряд). Снимается в конце загрузки.").Value
                && !BtcCompat.Present; // BetterThanCastle делает то же самое сам
            if (Config.Bind("Performance", "LessGarbage", true,
                    "Убрать лишние выделения памяти в частых местах игры (реже сборка мусора).").Value)
                GcFixes.Apply(_harmony);
            if (Config.Bind("Performance", "OverheadSortOnlyOnChange", true,
                    "Плашки над головами меняют порядок отрисовки только когда он реально изменился (меньше пересортировки интерфейса).").Value)
                UiTweaks.Apply(_harmony);
            if (!PortCompat.Active && Config.Bind("Performance", "ThrottleStaticSounds", true,
                    "Неподвижные звуковые объекты (хранилища, постройки) сверяют позицию раз в 15 кадров, а не каждый кадр.").Value)
                SoundThrottle.Apply(_harmony);
            if (!PortCompat.Active && Config.Bind("AI", "StorageReservations", true,
                    "Бронь места в хранилище: двое не бегут сложить ресурсы туда, где места хватит одному.").Value)
                StorageReservations.Apply(_harmony);
            _fastExit = Config.Bind("Compat", "FastExit", true,
                "При выходе из игры (Alt+F4, «Выход») сразу завершать процесс: без зависания и аварийного падения старого Mono.").Value;
            if (Config.Bind("AI", "FasterGoalCounting", true,
                    "Быстрый подсчёт целей добычи (без мусора) — бриктроны дешевле выбирают работу.").Value)
                AiPerf.Apply(_harmony);
            if (!PortCompat.Active && Config.Bind("AI", "StuckDetection", true, "Уведомлять о застрявших бриктронах.").Value)
            {
                StuckWatcher.Register(_harmony);
                _stuck = new StuckWatcher(Config.Bind("AI", "StuckAutoDig", true,
                    "Застрявший бриктрон, который умеет копать, сам копает выход (только природный грунт).").Value);
            }

            // 0.12: выключено по умолчанию (новый ключ) — замеры показали, что фризы были не от физики, а от проверок мода.
            _movingFix = new MovingColliderFix(Config.Bind("Performance", "KinematicMovingColliders2", false,
                "Движущимся статичным коллайдерам давать кинематическое тело. Эксперимент, пользы не показал. «-» в игре — вкл/выкл.").Value);

            if (Config.Bind("Performance", "FixColliderScaler", true,
                    "Не переписывать радиус коллайдеров 50 раз в секунду без изменений (нагрузка на физику).").Value)
                PhysicsPatches.Apply(_harmony);

            Logger.LogInfo(Compat.SystemSummary());
            if (!PortCompat.Active) Compat.Apply(_harmony,
                Config.Bind("Compat", "HighResTimer", true,
                    "Точность таймера Windows 1 мс: ровнее ограничитель FPS (Windows 11 по умолчанию даёт игре 15.6 мс).").Value,
                Config.Bind("Compat", "MaxRefreshRate", true,
                    "В полном экране использовать максимальную частоту монитора (Unity 5.6 может выбрать 60 Гц).").Value);

            if (_enableProfiler.Value)
            {
                var showOnStart = Config.Bind("Profiler", "ShowOverlayOnStart", true,
                    "Показывать оверлей сразу при запуске (иначе — по клавише).");
                _profiler = new FrameProfiler(Paths.BepInExRootPath, _autoReportSeconds.Value);
                _profiler.ShowOverlay = showOnStart.Value;
                ProfilerPatches.TrackGarbage = Config.Bind("Profiler", "TrackGarbage", false,
                    "Считать мусор по каждому методу (раздел «Мусор» в отчёте). Дорого: +1–2 мс на кадр. Включать для расследований.").Value;
                var detailed = Config.Bind("Profiler", "DetailedHooks", true,
                    "Подробные замеры каждого Update/Lua/путей (~1300 перехватов за кадр, стоят ~0.5–1 мс). " +
                    "false — только FPS, рендер и фризы, почти бесплатно.");
                // На порте подробные перехваты почти не видят кадр (его делают системы Castle Story Plus, у них своё окно F7),
                // а 439 перехватов стоят ~1–2 мс — там остаются только FPS, этапы кадра и фризы.
                var detOnPort = Config.Bind("Profiler", "DetailedOnPort", false,
                    "На порте всё равно ставить подробные перехваты (для поиска мусора по методам: +1–2 мс на кадр). Нужен перезапуск игры.");
                bool det = PortCompat.On(detailed.Value, detOnPort.Value);
                ProfilerPatches.Installed = det;
                ProfilerPatches.Apply(_harmony, det);
                if (det) { TerrainColliderPatches.Profile(_harmony); PhysicsWatch.Apply(_harmony); AiPerf.Profile(_harmony); }
                StartCoroutine(FrameSplit.Loop());
                _btcProfile = det;
            }

            if (BtcCompat.Present && Config.Bind("BetterThanCastle", "Optimize", true,
                    "Оптимизировать мод BetterThanCastle (если установлен): убрать лишние проходы интерфейса и т.п.").Value)
            {
                BtcTuning.StatsRecInterval = Config.Bind("BetterThanCastle", "ReplayStatsInterval", 1f,
                    "Как часто их мод пишет статистику матча в реплей, с (у них 0.1 — каждая запись стоит 20–50 мс).").Value;
                BtcTuning.Apply(_harmony, _btcProfile);
                _btcTuning = true;
            }

            Logger.LogInfo(Name + " " + Version + " загружен.");
        }

        private MenuBranding _branding;
        private AnimatorCulling _animCulling;
        private MovingColliderFix _movingFix;
        private StuckWatcher _stuck;
        internal static bool AiFastDecisions;
        private bool _stationPriorities;

        // Собственное время мода — отдельными строками в отчёте, чтобы его не путать с физикой игры.
        private static Entry ModEntry(string name)
        {
            var e = new Entry { Name = "[Мод] " + name, Category = "Mod" };
            ProfilerPatches.All.Add(e);
            return e;
        }
        private static readonly Entry MStuck = ModEntry("застрявшие бриктроны");
        private static readonly Entry MMoving = ModEntry("движущиеся коллайдеры");
        private static readonly Entry MBranding = ModEntry("надпись в меню");
        private static readonly Entry MAnim = ModEntry("аниматоры");
        private static readonly Entry MStations = ModEntry("приоритеты мастерских");
        private static readonly Entry MProfiler = ModEntry("замерщик");
        private static readonly Entry MGui = ModEntry("OnGUI мода");

        private static long Stamp(Entry e, long t0)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long dt = now - t0;
            e.Ticks += dt; e.FrameTicks += dt; e.Calls++;
            if (dt > e.MaxTicks) e.MaxTicks = dt;
            return now;
        }

        /// <summary>PageUp/PageDown заняты меню BetterThanCastle — там приоритет мастерской только из кругового меню.</summary>
        private static KeyCode BtcKey(KeyCode k)
        {
            return BtcCompat.Present && (k == KeyCode.PageUp || k == KeyCode.PageDown) ? KeyCode.None : k;
        }

        private void Update()
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            // Наш Update может выполниться раньше игровых — тогда его время ошибочно попадало в «физику».
            if (FrameSplit.FrameFirst == 0) FrameSplit.FrameFirst = t;
            if (FrameSplit.FirstUpdate == 0) FrameSplit.FirstUpdate = t;

            LoadingPatches.Watchdog();
            GcControl.Watchdog();
            Compat.Tick();
            if (_btcTuning) BtcTuning.Tick();
            if (_stuck != null) { _stuck.Tick(); t = Stamp(MStuck, t); }
            if (_movingFix != null)
            {
                _movingFix.Tick();
                if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) _movingFix.Toggle();
                t = Stamp(MMoving, t);
            }
            if (_branding != null) { _branding.Tick(); t = Stamp(MBranding, t); }
            if (_animCulling != null)
            {
                _animCulling.Tick();
                if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus)) _animCulling.Toggle();
                t = Stamp(MAnim, t);
            }
            if (_stationPriorities) { StationPriorities.Tick(); t = Stamp(MStations, t); }
            if (_profiler == null) return;
            if (PortCompat.KeyDown(_overlayKey.Value)) _profiler.ShowOverlay = !_profiler.ShowOverlay;
            if (PortCompat.KeyDown(_dumpKey.Value)) _profiler.WriteReport("ручной отчёт (F9)");
            if (PortCompat.KeyDown(KeyCode.F7))
            {
                ProfilerPatches.Active = !ProfilerPatches.Active;
                Logger.LogInfo("Подробный замер " + (ProfilerPatches.Active ? "ВКЛ" : "ВЫКЛ") + " (F7)");
            }
            _profiler.Tick();
            Stamp(MProfiler, t); // попадёт в следующий кадр: Tick обнуляет покадровые счётчики
        }

        private void OnGUI()
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_branding != null) _branding.DrawFallback();
            if (_stuck != null) _stuck.DrawGUI();
            if (_stationPriorities) StationPriorities.DrawGUI();
            if (_profiler != null) _profiler.DrawOverlay();
            Stamp(MGui, t);
        }

        internal void OnLoadFinished(string report)
        {
            if (_profiler != null) _profiler.AppendRaw(report);
        }

        private void OnApplicationQuit()
        {
            if (_profiler != null) _profiler.WriteReport("выход из игры");
            Compat.Shutdown();
            if (!_fastExit) return;
            // Все OnApplicationQuit игры (статистика, сеть, звук) выполняются в этом же кадре — даём им 0.5 с,
            // затем завершаем процесс сами: дальше Unity 5.6 при выгрузке зависает и падает в финализаторе Mono.
            try { PlayerPrefs.Save(); } catch { }
            Logger.LogInfo("Быстрый выход: процесс будет завершён через 0.5 с.");
            PortCompat.CspSaving(); // поиск типов — в главном потоке
            var t = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(500);
                // Castle Story Plus дописывает сохранение в своём потоке — ждём его (до 30 с), иначе файл обрежется.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (PortCompat.CspSaving() && sw.ElapsedMilliseconds < 30000) System.Threading.Thread.Sleep(100);
                try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
            });
            t.IsBackground = true;
            t.Start();
        }
        private bool _fastExit;
        private bool _btcProfile, _btcTuning;
    }
}
