using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using CastleStoryModInstaller;

namespace CastleStoryModSettings
{
    /// <summary>
    /// Меню перед запуском игры: два блока — функции Castle Story Plus ([Features] в их конфиге) и функции нашего мода
    /// dodoMAKSharmony (castlestory.optimizer.cfg), в каждом свои подгруппы (геймплей, интерфейс, оптимизация…).
    /// Отметка = включено. Кнопка «Как в оригинале» выключает все геймплейные изменения в обоих блоках.
    /// Правятся только конфиги модов — файлы игры не трогаем.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private sealed class Feature
        {
            public int Block; // 0 — Castle Story Plus, 1 — наш мод
            public string Key, Section, Description, Ru, Group;
            public bool Value, Default;
            public int Line = -1; // строка в конфиге; -1 — записи ещё нет, допишем при сохранении
        }

        private const string CfgName = "com.tricky12321.castlestoryplus.cfg";
        private const string ModCfgName = "castlestory.optimizer.cfg";
        private const string Gameplay = "Геймплей: ";

        private static readonly string[] BlockTitles = { "CASTLE STORY PLUS (мод автора Tricky12321)", "DODOMAKSHARMONY (наш мод)" };

        private readonly string _gameDir;
        private string _cfgPath, _modCfgPath;
        private string[] _lines;
        private List<string> _modLines = new List<string>();
        private readonly List<Feature> _features = new List<Feature>();
        private readonly Dictionary<string, string> _ru = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly TreeView _tree = new TreeView();
        private readonly TextBox _desc = new TextBox();
        private readonly Label _status = new Label();
        private bool _busy;

        // Подгруппы Castle Story Plus
        private static readonly string[][] Groups =
        {
            new[] { Gameplay + "рабочие и задачи",
                "AssignNow AutoCleanup AutoRepair BaseJobs BuildJobs StuckReset DirectTask WorkerAI TaskReservation JobBoard LargerTasks LoadRelief PauseTasks PickUpAround FasterTaskSearch PriorityLevels QuickNextTask ResourceReservation StockpileChoice StockpileConsolidation StockpilePickup StuckEscape TaskFlow TaskPrediction UnreachableTasks WorkLimit Pathfinding PassThroughAllies QuarrySpeed QuarryDepth QuarryLimit" },
            new[] { Gameplay + "мастерские и крафт",
                "WorkshopFetchRange WorkshopPriority WorkshopSetup CraftLoopLimit CraftRoundRobin WorkSpotReserve KitBundles KitReservation Market Upgrades Metallurgy" },
            new[] { Gameplay + "бой и вторжение",
                "ArcherAccuracy ArtificerHealing CriticalHits Healtron PlayerOrdersFirst RangedReposition Retreat UnarmedWorkers WallLean CallToArms RangedGuard SoldierPosts EnemyTreeClearing SpawnDistance WaveInterval DarkCrystals GiantBricktron Experience WiderVision DropGearOnDeath" },
            new[] { Gameplay + "стройка и постройки",
                "CustomBlocks LargeStockpile MixedRacks MixedStockpiles MoveStructure RackFilter SidePlanks StockpileSpill Warehouse WoodOverhang Ladders DoorRepair CopyPaste GlobalBuildJob BuildPriority EmptyStockpile" },
            new[] { Gameplay + "мир, ресурсы и экономика",
                "RandomWorld Treasure Economy RespawnCost Saplings TreeStumps WildPlants WornPaths BlueCrystalDeposits LeanCoal MineShafts OreMine RichIron" },
            new[] { "Интерфейс и удобство",
                "CameraZoomOut EnergyHint FastStartup ItemHint MenuTextFields MinimapTerrain MinimapZoom PlusLogo ResourceList StockpileTooltip WardRangeFade WaveDirection WaveWarning WorldSearch RespawnStatus ContinueButton SaveAndLeave SpeedKeys BlockAnchor BlueprintsVisible BuildBlocked BuildNeeds BuildShortage DeconstructColour DragRelease Eyedropper UnreachableThought FocusInputGuard UpdateCheck LauncherOptions CsMenus" },
            new[] { "Производительность и исправления",
                "RenderTuning FrameSavers FasterLoading GameLeakFixes LuaHeapShrink LuaStackShrink AutoSave FastSave FastQuit CraftFinishFix" },
            new[] { "Мультиплеер",
                "AllyWorkerIcons DesyncProtection GameChat HiddenDeliveries LobbyStart MaxPlayers SavedTeams SteamRelay" },
            new[] { "Отладка",
                "DebugMenu Performance SystemLog IdleLog" },
        };

        // Функции нашего мода: раздел конфига, ключ, подгруппа, название, по умолчанию, пометка
        private static readonly string[][] Ours =
        {
            new[] { "Performance", "LessGarbage", "Оптимизация и мусор", "Меньше мусора в частых местах игры (реже сборки мусора)", "true", "" },
            new[] { "Performance", "OverheadSortOnlyOnChange", "Оптимизация и мусор", "Плашки над головами меняют порядок отрисовки только при изменении", "true", "" },
            new[] { "Performance", "FixColliderScaler", "Оптимизация и мусор", "Не переписывать радиус коллайдеров 50 раз в секунду", "true", "" },
            new[] { "Gc", "SliceGc", "Оптимизация и мусор", "Сборка мусора кусками по 2 мс за кадр вместо паузы 150–200 мс (порт, Unity 6)", "true", "" },
            new[] { "AI", "FasterGoalCounting", "Оптимизация и мусор", "Быстрый подсчёт целей добычи без мусора", "true", "" },
            new[] { "Fixes", "FastLoading", "Загрузка и выход", "Снять лимит FPS и vsync на время загрузки мира", "true", "На порте загрузку ускоряет Castle Story Plus, эта опция там не действует." },
            new[] { "Fixes", "CollectGarbageAfterLoad", "Загрузка и выход", "Убрать мусор в конце загрузки, на экране загрузки", "true", "На порте не используется." },
            new[] { "Fixes", "FastLoadWaits", "Загрузка и выход", "Убрать паузы по 1 с и искусственное догоняние полосы прогресса", "true", "На порте не используется." },
            new[] { "Compat", "FastExit", "Загрузка и выход", "Быстрый выход по Alt+F4 и «Выход» (на порте ждёт сохранение Castle Story Plus)", "true", "" },
            new[] { "Fixes", "DisableAnalytics", "Загрузка и выход", "Отключить мёртвую аналитику GameAnalytics (меньше лишних запросов)", "true", "" },
            new[] { "AI", "FastDecisions", Gameplay + "ИИ и мастерские", "Бриктроны быстрее выбирают работу (очередь раздумий короче)", "true", "На порте может не действовать: там это делает Castle Story Plus." },
            new[] { "Workshops", "Enabled", Gameplay + "ИИ и мастерские", "Приоритеты мастерских (клавиши Page Up / Page Down)", "true", "" },
            new[] { "Workshops", "ShowLabels", Gameplay + "ИИ и мастерские", "Подписи приоритета над мастерскими", "true", "" },
            new[] { "Workshops", "PieMenuButton", Gameplay + "ИИ и мастерские", "Кнопка «Приоритет» в круговом меню мастерской", "true", "" },
            new[] { "Language", "ForceRussian", "Язык", "Всегда включать русский перевод при запуске", "true", "" },
            new[] { "Language", "TranslateCastleStoryPlus", "Язык", "Переводить на русский Castle Story Plus (меню, подсказки, настройки)", "true", "" },
            new[] { "Profiler", "Enabled", "Замерщик и диагностика", "Замерщик кадров и отчёты (оверлей, файлы отчётов)", "true", "" },
            new[] { "Profiler", "ShowOverlayOnStart", "Замерщик и диагностика", "Показывать оверлей замерщика сразу при запуске", "true", "" },
            new[] { "Profiler", "DetailedHooks", "Замерщик и диагностика", "Подробные замеры каждого Update, Lua и путей", "true", "" },
            new[] { "Profiler", "DetailedOnPort", "Замерщик и диагностика", "То же на порте (стоит 1–2 мс на кадр, нужен перезапуск)", "false", "" },
            new[] { "Profiler", "TrackGarbage", "Замерщик и диагностика", "Считать мусор по методам (дорого: +1–2 мс на кадр)", "false", "" },
            new[] { "Performance", "KinematicMovingColliders2", "Эксперименты", "Движущимся статичным коллайдерам давать кинематическое тело (эксперимент)", "false", "" },
        };

        private static readonly string[] OptimizationKeys =
        {
            "FasterTaskSearch", "Pathfinding", "JobBoard", "UnreachableTasks", "TaskReservation", "ResourceReservation",
            "LoadRelief", "StuckReset", "StuckEscape", "PassThroughAllies",
        };

        public SettingsForm(string[] args)
        {
            Text = "dodoMAKSharmony — настройки модов";
            Size = new Size(1000, 740);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);

            _gameDir = GameLocator.Find();
            LoadRu();
            if (_gameDir == null || !LoadCfg()) { BuildError(); return; }
            LoadModCfg();
            BuildUi();
            FillTree();
            // проверка без экрана: --shot файл.png [--original] [--block2] — рисует окно в картинку и выходит
            int si = Array.IndexOf(args, "--shot");
            if (si >= 0 && si + 1 < args.Length)
            {
                var path = args[si + 1];
                if (Array.IndexOf(args, "--original") >= 0) Preset(f => f.Group.StartsWith(Gameplay) ? false : (bool?)null);
                bool second = Array.IndexOf(args, "--block2") >= 0;
                Load += (s, e) =>
                {
                    var node = _tree.Nodes[second ? 1 : 0];
                    node.Expand();
                    _tree.SelectedNode = node.Nodes[0].Nodes.Count > 0 ? node.Nodes[0].Nodes[0] : node.Nodes[0];
                    if (second) { _tree.Nodes[0].Collapse(); node.Nodes[0].Expand(); }
                    var bmp = new Bitmap(Width, Height);
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(path);
                    Close();
                };
            }
        }

        private void BuildError()
        {
            var l = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Text = _gameDir == null
                    ? "Castle Story не найден в библиотеках Steam."
                    : "Не найден конфиг Castle Story Plus:\n" + CfgName + "\n\nЗапусти игру через порт один раз — он создастся."
            };
            Controls.Add(l);
        }

        private void LoadRu()
        {
            try
            {
                using (var s = typeof(SettingsForm).Assembly.GetManifestResourceStream("csp_ru.txt"))
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0) _ru[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1));
                    }
                }
            }
            catch { }
        }

        private static string Unescape(string s)
        {
            return s.Replace("\\t", "\t").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\");
        }

        private bool LoadCfg()
        {
            foreach (var rel in new[] { @"CastleStoryPlus-Unity6\BepInEx\config", @"BepInEx\config" })
            {
                var p = Path.Combine(_gameDir, rel, CfgName);
                if (File.Exists(p)) { _cfgPath = p; break; }
            }
            if (_cfgPath == null) return false;
            _modCfgPath = Path.Combine(Path.GetDirectoryName(_cfgPath), ModCfgName);
            _lines = File.ReadAllLines(_cfgPath, Encoding.UTF8);
            bool inFeatures = false;
            string desc = null, def = null;
            for (int i = 0; i < _lines.Length; i++)
            {
                var l = _lines[i].Trim();
                if (l.StartsWith("[")) { inFeatures = l == "[Features]"; continue; }
                if (!inFeatures) continue;
                if (l.StartsWith("## ")) { desc = l.Substring(3); continue; }
                if (l.StartsWith("# Default value:")) { def = l.Substring(16).Trim(); continue; }
                int eq = l.IndexOf('=');
                if (l.Length == 0 || l[0] == '#' || eq <= 0) continue;
                var key = l.Substring(0, eq).Trim();
                var val = l.Substring(eq + 1).Trim();
                string ru;
                _features.Add(new Feature
                {
                    Block = 0, Key = key, Description = desc ?? "", Line = i,
                    Ru = desc != null && _ru.TryGetValue(desc, out ru) ? ru : desc ?? "",
                    Value = val.Equals("true", StringComparison.OrdinalIgnoreCase),
                    Default = def == null || def.Equals("true", StringComparison.OrdinalIgnoreCase),
                    Group = GroupOf(key),
                });
                desc = null; def = null;
            }
            return _features.Count > 0;
        }

        /// <summary>Конфиг нашего мода: значения читаем из файла, недостающие записи допишем при сохранении.</summary>
        private void LoadModCfg()
        {
            if (File.Exists(_modCfgPath)) _modLines = new List<string>(File.ReadAllLines(_modCfgPath, Encoding.UTF8));
            foreach (var o in Ours)
            {
                var f = new Feature
                {
                    Block = 1, Section = o[0], Key = o[1], Group = o[2], Default = o[4] == "true", Value = o[4] == "true",
                    Ru = o[3] + (o[5].Length > 0 ? "\r\n\r\n" + o[5] : ""),
                };
                f.Description = f.Ru;
                string section = null;
                for (int i = 0; i < _modLines.Count; i++)
                {
                    var l = _modLines[i].Trim();
                    if (l.StartsWith("[")) { section = l.Substring(1, l.Length - 2); continue; }
                    if (section != f.Section || l.Length == 0 || l[0] == '#') continue;
                    int eq = l.IndexOf('=');
                    if (eq <= 0 || l.Substring(0, eq).Trim() != f.Key) continue;
                    f.Line = i;
                    f.Value = l.Substring(eq + 1).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                }
                _features.Add(f);
            }
        }

        private static string GroupOf(string key)
        {
            foreach (var g in Groups)
                foreach (var k in g[1].Split(' '))
                    if (k == key) return g[0];
            return "Прочее";
        }

        private void BuildUi()
        {
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
            top.Controls.Add(Btn("Как в оригинале", "Выключить ВСЕ геймплейные изменения в обоих модах. Интерфейс, ускорения и исправления остаются.", (s, e) => Preset(f => f.Group.StartsWith(Gameplay) ? false : (bool?)null)));
            top.Controls.Add(Btn("Включить оптимизацию", "Включить всё про скорость: оптимизации и исправления обоих модов и функции ИИ Castle Story Plus, которые разгружают процессор. Остальное не трогать.", (s, e) => Preset(OptimizationRule)));
            top.Controls.Add(Btn("Рекомендуемые", "Как задумали авторы: значения по умолчанию.", (s, e) => Preset(f => (bool?)f.Default)));
            top.Controls.Add(Btn("Включить всё", "Включить все функции (кроме экспериментов и тяжёлой диагностики нашего мода).", (s, e) => Preset(f => (bool?)(f.Block == 0 || f.Default))));
            top.Controls.Add(Btn("Выключить всё", "Выключить вообще все функции обоих модов из списка.", (s, e) => Preset(f => (bool?)false)));

            _tree.Dock = DockStyle.Fill;
            _tree.CheckBoxes = true;
            _tree.ShowLines = false;
            _tree.ItemHeight = 22;
            _tree.AfterCheck += TreeAfterCheck;
            _tree.AfterSelect += (s, e) => ShowDesc(e.Node);

            _desc.Dock = DockStyle.Bottom;
            _desc.Height = 96;
            _desc.Multiline = true;
            _desc.ReadOnly = true;
            _desc.ScrollBars = ScrollBars.Vertical;
            _desc.BackColor = SystemColors.Window;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
            var play = Btn("Сохранить и запустить игру", null, (s, e) => { if (Save()) Launch(); });
            play.Width = 230;
            bottom.Controls.Add(play);
            bottom.Controls.Add(Btn("Сохранить", "Записать выбор в конфиги (вступит в силу при следующем запуске игры).", (s, e) => Save()));
            _status.AutoSize = true; _status.Padding = new Padding(0, 8, 12, 0);
            bottom.Controls.Add(_status);

            Controls.Add(_tree);
            Controls.Add(_desc);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        private static bool? OptimizationRule(Feature f)
        {
            if (f.Block == 0)
            {
                if (f.Group == "Производительность и исправления" || Array.IndexOf(OptimizationKeys, f.Key) >= 0 || f.Key == "CsMenus") return true;
                return null;
            }
            if (f.Group == "Оптимизация и мусор" || f.Group == "Загрузка и выход") return true;
            return null;
        }

        private Button Btn(string text, string tip, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(130, 30), Padding = new Padding(6, 0, 6, 0) };
            b.Click += click;
            if (tip != null) new ToolTip { AutoPopDelay = 8000 }.SetToolTip(b, tip);
            return b;
        }

        private void FillTree()
        {
            _busy = true;
            _tree.Nodes.Clear();
            for (int block = 0; block < 2; block++)
            {
                var root = new TreeNode { Text = BlockTitles[block] };
                var order = new List<string>();
                if (block == 0) { foreach (var g in Groups) order.Add(g[0]); order.Add("Прочее"); }
                else foreach (var g in new[] { "Оптимизация и мусор", "Загрузка и выход", Gameplay + "ИИ и мастерские", "Язык", "Замерщик и диагностика", "Эксперименты" }) order.Add(g);
                foreach (var g in order)
                {
                    TreeNode parent = null;
                    foreach (var f in _features)
                    {
                        if (f.Block != block || f.Group != g) continue;
                        if (parent == null) parent = new TreeNode { Text = g.ToUpperInvariant() };
                        parent.Nodes.Add(new TreeNode { Text = f.Key + " — " + Short(f.Ru), Tag = f, Checked = f.Value });
                    }
                    if (parent == null) continue;
                    RefreshNode(parent);
                    root.Nodes.Add(parent);
                    if (g.StartsWith(Gameplay) || block == 1) parent.Expand();
                }
                RefreshNode(root);
                _tree.Nodes.Add(root);
                root.Expand();
            }
            _busy = false;
            UpdateStatus();
        }

        private static string Short(string s)
        {
            int nl = s.IndexOf('\r');
            if (nl > 0) s = s.Substring(0, nl);
            int dot = s.IndexOf(". ");
            if (dot > 20 && dot < 85) return s.Substring(0, dot + 1);
            return s.Length > 85 ? s.Substring(0, 85).TrimEnd() + "…" : s;
        }

        private void ShowDesc(TreeNode n)
        {
            var f = n == null ? null : n.Tag as Feature;
            _desc.Text = f == null ? "" : f.Key + (f.Default ? "" : "   (по умолчанию выключено)") + "\r\n" + f.Ru;
        }

        private static void RefreshNode(TreeNode p)
        {
            bool all = true;
            foreach (TreeNode c in p.Nodes) if (!c.Checked) { all = false; break; }
            p.Checked = all;
        }

        private static void SetDown(TreeNode n, bool value)
        {
            n.Checked = value;
            var f = n.Tag as Feature;
            if (f != null) f.Value = value;
            foreach (TreeNode c in n.Nodes) SetDown(c, value);
        }

        private void TreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            SetDown(e.Node, e.Node.Checked);
            for (var p = e.Node.Parent; p != null; p = p.Parent) RefreshNode(p);
            _busy = false;
            UpdateStatus();
        }

        private void Preset(Func<Feature, bool?> rule)
        {
            foreach (var f in _features) { var v = rule(f); if (v.HasValue) f.Value = v.Value; }
            FillTree();
        }

        private void UpdateStatus()
        {
            int on = 0, gp = 0, gpOn = 0;
            foreach (var f in _features)
            {
                if (f.Value) on++;
                if (f.Group.StartsWith(Gameplay)) { gp++; if (f.Value) gpOn++; }
            }
            _status.Text = "Включено " + on + " из " + _features.Count + "; геймплейных " + gpOn + " из " + gp;
        }

        private bool Save()
        {
            try
            {
                foreach (var f in _features)
                    if (f.Block == 0) _lines[f.Line] = f.Key + " = " + (f.Value ? "true" : "false");
                File.Copy(_cfgPath, _cfgPath + ".bak", true);
                File.WriteAllLines(_cfgPath, _lines, new UTF8Encoding(false));

                foreach (var f in _features)
                {
                    if (f.Block != 1) continue;
                    string text = f.Key + " = " + (f.Value ? "true" : "false");
                    if (f.Line >= 0) { _modLines[f.Line] = text; continue; }
                    int at = _modLines.FindIndex(l => l.Trim() == "[" + f.Section + "]");
                    if (at < 0) { _modLines.Add(""); _modLines.Add("[" + f.Section + "]"); _modLines.Add(text); f.Line = _modLines.Count - 1; }
                    else
                    {
                        _modLines.Insert(at + 1, text);
                        foreach (var g in _features) if (g.Block == 1 && g.Line > at) g.Line++;
                        f.Line = at + 1;
                    }
                }
                if (File.Exists(_modCfgPath)) File.Copy(_modCfgPath, _modCfgPath + ".bak", true);
                File.WriteAllLines(_modCfgPath, _modLines, new UTF8Encoding(false));
                _status.Text = "Сохранено. Действует со следующего запуска игры.";
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить (закрой игру?):\n" + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void Launch()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(_gameDir, GameLocator.ExeName)) { WorkingDirectory = _gameDir, UseShellExecute = true });
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, "Не удалось запустить игру:\n" + ex.Message); }
        }
    }
}
