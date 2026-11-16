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
    /// Меню перед запуском игры: список функций Castle Story Plus ([Features] в их конфиге) с русскими описаниями,
    /// отметка = включено. Геймплейные изменения собраны по группам; кнопка «Как в оригинале» выключает их все
    /// (интерфейс, исправления утечек и ускорения остаются). Правится только сам конфиг CSP — файлы игры не трогаем.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private sealed class Feature
        {
            public string Key, Description, Ru, Group;
            public bool Value, Default;
            public int Line;
        }

        private const string CfgName = "com.tricky12321.castlestoryplus.cfg";
        private const string Gameplay = "Геймплей: ";

        private readonly string _gameDir;
        private string _cfgPath;
        private string[] _lines;
        private readonly List<Feature> _features = new List<Feature>();
        private readonly Dictionary<string, string> _ru = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly TreeView _tree = new TreeView();
        private readonly TextBox _desc = new TextBox();
        private readonly Label _status = new Label();
        private bool _busy;

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

        public SettingsForm(string[] args)
        {
            Text = "dodoMAKSharmony — настройки Castle Story Plus";
            Size = new Size(980, 720);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);

            _gameDir = GameLocator.Find();
            LoadRu();
            if (_gameDir == null || !LoadCfg()) { BuildError(); return; }
            BuildUi();
            FillTree();
            // проверка без экрана: --shot файл.png [--original] — рисует окно в картинку и выходит
            int si = Array.IndexOf(args, "--shot");
            if (si >= 0 && si + 1 < args.Length)
            {
                var path = args[si + 1];
                if (Array.IndexOf(args, "--original") >= 0) Preset(f => f.Group.StartsWith(Gameplay) ? false : (bool?)null);
                Load += (s, e) =>
                {
                    _tree.SelectedNode = _tree.Nodes[0].Nodes[0];
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
                    Key = key, Description = desc ?? "", Line = i,
                    Ru = desc != null && _ru.TryGetValue(desc, out ru) ? ru : desc ?? "",
                    Value = val.Equals("true", StringComparison.OrdinalIgnoreCase),
                    Default = def == null || def.Equals("true", StringComparison.OrdinalIgnoreCase),
                    Group = GroupOf(key),
                });
                desc = null; def = null;
            }
            return _features.Count > 0;
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
            top.Controls.Add(Btn("Как в оригинале", "Выключить ВСЕ геймплейные изменения. Интерфейс, ускорения и исправления утечек остаются.", (s, e) => Preset(f => f.Group.StartsWith(Gameplay) ? false : (bool?)null)));
            top.Controls.Add(Btn("Рекомендуемые", "Как задумал автор Castle Story Plus (кроме сундуков со зверями).", (s, e) => Preset(f => f.Key == "Treasure" ? false : (bool?)f.Default)));
            top.Controls.Add(Btn("Включить всё", "Включить всё, кроме сундуков со зверями.", (s, e) => Preset(f => (bool?)(f.Key != "Treasure"))));
            top.Controls.Add(Btn("Выключить всё", "Выключить вообще все функции Castle Story Plus из списка.", (s, e) => Preset(f => (bool?)false)));

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
            bottom.Controls.Add(Btn("Сохранить", "Записать выбор в конфиг (вступит в силу при следующем запуске игры).", (s, e) => Save()));
            _status.AutoSize = true; _status.Padding = new Padding(0, 8, 12, 0);
            bottom.Controls.Add(_status);

            Controls.Add(_tree);
            Controls.Add(_desc);
            Controls.Add(bottom);
            Controls.Add(top);
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
            var order = new List<string>();
            foreach (var g in Groups) order.Add(g[0]);
            order.Add("Прочее");
            foreach (var g in order)
            {
                TreeNode parent = null;
                foreach (var f in _features)
                {
                    if (f.Group != g) continue;
                    if (parent == null) parent = new TreeNode { Text = "" + g.ToUpperInvariant() };
                    parent.Nodes.Add(new TreeNode { Text = f.Key + " — " + Short(f.Ru), Tag = f, Checked = f.Value });
                }
                if (parent == null) continue;
                RefreshParent(parent);
                _tree.Nodes.Add(parent);
                if (g.StartsWith(Gameplay)) parent.Expand();
            }
            _busy = false;
            UpdateStatus();
        }

        private static string Short(string s)
        {
            int dot = s.IndexOf(". ");
            if (dot > 20 && dot < 85) return s.Substring(0, dot + 1);
            return s.Length > 85 ? s.Substring(0, 85).TrimEnd() + "…" : s;
        }

        private void ShowDesc(TreeNode n)
        {
            var f = n == null ? null : n.Tag as Feature;
            _desc.Text = f == null ? "" : f.Key + (f.Default ? "" : "   (по умолчанию выключено)") + "\r\n" + f.Ru;
        }

        private static void RefreshParent(TreeNode p)
        {
            bool all = true;
            foreach (TreeNode c in p.Nodes) if (!c.Checked) { all = false; break; }
            p.Checked = all;
        }

        private void TreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            if (e.Node.Tag is Feature) ((Feature)e.Node.Tag).Value = e.Node.Checked;
            else foreach (TreeNode c in e.Node.Nodes) { c.Checked = e.Node.Checked; ((Feature)c.Tag).Value = e.Node.Checked; }
            if (e.Node.Parent != null) RefreshParent(e.Node.Parent);
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
                    _lines[f.Line] = f.Key + " = " + (f.Value ? "true" : "false");
                File.Copy(_cfgPath, _cfgPath + ".bak", true);
                File.WriteAllLines(_cfgPath, _lines, new UTF8Encoding(false));
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
