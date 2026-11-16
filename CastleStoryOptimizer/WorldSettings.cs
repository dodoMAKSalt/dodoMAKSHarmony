using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Brix.Assets;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Rules;
using Brix.UI.Builder.Menu;
using Brix.UI.Builder.Panel;
using Brix.UI.Icons;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Подробные настройки мира: кнопка в лобби (под выбором карты/режима/пресета) открывает окно с настройками.
    ///
    /// Как применяется (файлы игры не меняем):
    ///  • Lua-правила режима (число юнитов, стартовые рабочие, время суток, враги/союзники, самоцветы…) — после загрузки
    ///    config.lua и пресета (Asset_Gamemode.ApplyPresetSettingsToScript) перезаписываем нужные ключи sv_Settings.
    ///    Ключ, которого у режима нет, пропускается.
    ///  • Цена юнита, надбавка, цена возрождения — поля GeneralRules (SyncVar: хост пишет, клиентам уходит само).
    ///  • Добыча энергии: Brewery.ProcessFirefly — энергия новой «светлячка» умножается на коэффициент
    ///    (общий × по числу захваченных кристаллов, которые варят энергию).
    /// В сетевой игре главные значения — у хоста; число юнитов и время суток считаются у каждого игрока по своим настройкам,
    /// поэтому у всех должны быть одинаковые значения.
    /// </summary>
    internal static class WorldSettings
    {
        // ---------- описание настроек ----------
        internal sealed class Opt
        {
            public string Id, Group, Name, Hint, Lua;
            public float Min, Max, Step, Def;
            public bool Bool;
            public string[] Choices;
            public bool On;
            public float Value;
        }

        private static readonly List<Opt> All = new List<Opt>();
        private static Opt Add(string group, string id, string lua, string name, string hint, float min, float max, float step, float def)
        {
            var o = new Opt { Group = group, Id = id, Lua = lua, Name = name, Hint = hint, Min = min, Max = max, Step = step, Def = def, Value = def };
            All.Add(o);
            return o;
        }
        private static Opt AddBool(string group, string id, string lua, string name, string hint, bool def)
        {
            var o = Add(group, id, lua, name, hint, 0, 1, 1, def ? 1 : 0);
            o.Bool = true;
            return o;
        }

        private static Opt Cap, Workers, Knights, Archers;
        private static Opt UBase, UStep, UFree, RMul, RFlat, EMul;
        private static readonly Opt[] ECount = new Opt[6];
        private static Opt StartTime, DayF, NightF, Pause, Relations, Dig;
        private static Opt PumpFirst, Pump, ToWin, PerWave;

        private const string GUnits = "Юниты", GEnergy = "Энергия и цена юнитов", GCrystals = "Добыча энергии по числу кристаллов",
            GTime = "Время суток", GRules = "Правила", GGems = "Захват самоцветов";

        static WorldSettings()
        {
            Cap = Add(GUnits, "cap", "bricktronCap", "Макс. юнитов у каждого игрока", "Предел численности (в режимах обычно 12–15).", 1, 300, 1, 15);
            Workers = Add(GUnits, "workers", "startingWorkersCount", "Стартовые рабочие", "Сколько бриктронов у игрока в начале.", 0, 60, 1, 5);
            Knights = Add(GUnits, "knights", "startingKnightCount", "Стартовые рыцари", "Только Вторжение и Завоевание.", 0, 30, 1, 1);
            Archers = Add(GUnits, "archers", "startingArcherCount", "Стартовые лучники", "Только Вторжение и Завоевание.", 0, 30, 1, 1);

            UBase = Add(GEnergy, "ubase", null, "Цена нового юнита (энергия)", "Базовая цена появления нового бриктрона (в игре 100).", 10, 2000, 10, 100);
            UStep = Add(GEnergy, "ustep", null, "Надбавка за каждого следующего юнита", "Насколько дорожает следующий юнит (в игре 60; в «Захвате самоцветов» 0).", 0, 500, 5, 60);
            UFree = Add(GEnergy, "ufree", null, "Юнитов до начала надбавки", "Пока юнитов не больше этого числа, цена не растёт (в игре 6).", 0, 100, 1, 6);
            RMul = Add(GEnergy, "rmul", null, "Цена возрождения погибшего (множитель)", "Во сколько раз возрождение дороже/дешевле цены нового юнита. 0 = бесплатно.", 0, 5, 0.05f, 1);
            RFlat = Add(GEnergy, "rflat", null, "Штраф энергии за потерю юнита", "Сколько энергии дополнительно уходит за каждого погибшего.", 0, 2000, 10, 0);
            EMul = Add(GEnergy, "emul", null, "Общий множитель добычи энергии", "Умножает всю энергию, которую приносят кристаллы.", 0.1f, 10, 0.1f, 1);

            for (int i = 0; i < 6; i++)
                ECount[i] = Add(GCrystals, "ecount" + (i + 1), null,
                    i < 5 ? "Захвачено кристаллов: " + (i + 1) : "Захвачено кристаллов: 6 и больше",
                    "Множитель добычи энергии у игрока с таким числом захваченных кристаллов.", 0.1f, 10, 0.1f, 1);

            StartTime = Add(GTime, "starttime", "startingTimeOfDay", "Время суток в начале (час)", "0 — полночь, 12 — полдень.", 0, 24, 0.5f, 7);
            DayF = Add(GTime, "dayf", "daytimeFactor", "Длина дня (множитель)", "Больше — день тянется дольше.", 0.1f, 6, 0.1f, 1.4f);
            NightF = Add(GTime, "nightf", "nighttimeFactor", "Длина ночи (множитель)", "Больше — ночь тянется дольше.", 0.1f, 6, 0.1f, 0.6f);
            Pause = AddBool(GTime, "pause", "pauseTimeOfDay", "Остановить время суток", "Время суток замирает на стартовом часе.", false);

            Relations = Add(GRules, "relations", "playerRelations", "Отношения игроков", "Для сетевой игры.", 0, 2, 1, 2);
            Relations.Choices = new[] { "Союзники", "Нейтралитет", "Враги" };
            Dig = AddBool(GRules, "dig", "canDigGround", "Разрешить копать землю", "Где есть такая настройка (Вторжение, Завоевание).", true);

            PumpFirst = Add(GGems, "pumpfirst", "firstPumpkinSpawnTime", "Первый самоцвет через (с)", "Время до появления первого самоцвета.", 30, 7200, 30, 720);
            Pump = Add(GGems, "pump", "pumpkinSpawnTime", "Следующие самоцветы раз в (с)", "Пауза между самоцветами.", 30, 3600, 30, 480);
            ToWin = Add(GGems, "towin", "pumpkinsToWin", "Самоцветов для победы", "Сколько самоцветов нужно собрать.", 1, 50, 1, 10);
            PerWave = Add(GGems, "perwave", "pumpkinsPerWave", "Самоцветов за раз", "Сколько самоцветов появляется за одну волну.", 1, 30, 1, 5);
        }

        private static string FilePath { get { return Path.Combine(BepInEx.Paths.ConfigPath, "dodoMAKSharmony-world.txt"); } }

        public static int ActiveCount
        {
            get { int n = 0; foreach (var o in All) if (o.On) n++; return n; }
        }

        // ---------- установка патчей ----------
        public static void Apply(Harmony h)
        {
            Load();
            try
            {
                var footer = AccessTools.PropertyGetter(typeof(BaseMapStarterMenu), "Footer");
                h.Patch(footer, postfix: new HarmonyMethod(typeof(WorldSettings), nameof(FooterPostfix)));
            }
            catch (Exception e) { Plugin.Log.LogError("Настройки мира: кнопка в лобби не добавлена: " + e); }
            try
            {
                var m = AccessTools.Method(typeof(Asset_Gamemode), "ApplyPresetSettingsToScript");
                h.Patch(m, postfix: new HarmonyMethod(typeof(WorldSettings), nameof(PresetPostfix)));
            }
            catch (Exception e) { Plugin.Log.LogError("Настройки мира: правила режима не будут меняться: " + e); }
            try
            {
                var nest = typeof(FireflyNest);
                var brew = AccessTools.Inner(nest, "Brewery");
                _breweryNest = AccessTools.Field(brew, "Nest");
                h.Patch(AccessTools.Method(brew, "ProcessFirefly"), postfix: new HarmonyMethod(typeof(WorldSettings), nameof(EnergyPostfix)));
                h.Patch(AccessTools.Method(nest, "Awake"), postfix: new HarmonyMethod(typeof(WorldSettings), nameof(NestAdd)));
                h.Patch(AccessTools.Method(nest, "OnRequested"), postfix: new HarmonyMethod(typeof(WorldSettings), nameof(NestAdd)));
                h.Patch(AccessTools.Method(nest, "OnDestroy"), postfix: new HarmonyMethod(typeof(WorldSettings), nameof(NestRemove)));
                h.Patch(AccessTools.Method(nest, "ResetComponent"), postfix: new HarmonyMethod(typeof(WorldSettings), nameof(NestRemove)));
                var feed = AccessTools.Method(nest, "FeedQueuedFireflies");
                h.Patch(feed, prefix: new HarmonyMethod(typeof(WorldSettings), nameof(FeedPrefix)) { priority = Priority.First });
                _getFirefly = AccessTools.Method(nest, "GetFireflyOfCondition");
                _consume = AccessTools.Method(nest, "ConsumeUnnamedFirefliesUpTo");
                _spawn = AccessTools.Method(nest, "SpawnBricktronFromFirefly");
            }
            catch (Exception e) { Plugin.Log.LogError("Настройки мира: энергия и возрождение не изменятся: " + e); }
            Plugin.Log.LogInfo("Настройки мира включены (кнопка в лобби под пресетом). Изменено пунктов: " + ActiveCount);
        }

        // ---------- кнопка в лобби ----------
        private static void FooterPostfix(object __instance, ref PanelList __result)
        {
            try
            {
                if (__result == null) return;
                var menu = __instance;
                Action<LegacyButtonPanelComponent> reset = delegate (LegacyButtonPanelComponent panel)
                {
                    panel._button.onClick.RemoveAllListeners();
                    panel._button.onClick.AddListener(delegate { Open(); });
                };
                Action<LegacyButtonPanelComponent> refresh = delegate (LegacyButtonPanelComponent panel) { RefreshButton(menu, panel); };
                var p = LegacyButtonPanelComponent.Build(reset, refresh).As(LegacyButtonPanelComponent.BasePanel.Variant.LegacyReskin_DblTxt_RightIcon);
                p.IgnoreWidgets();
                __result.Insert(Math.Min(3, __result.Count), p);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Настройки мира: кнопка не добавлена: " + e.Message); }
        }

        private static bool MenuFlag(object menu, string name)
        {
            try { return Traverse.Create(menu).Property(name).GetValue<bool>(); } catch { return false; }
        }

        private static void RefreshButton(object menu, LegacyButtonPanelComponent panel)
        {
            bool shown = false;
            try
            {
                var map = Traverse.Create(menu).Property("DisplayedMap").GetValue();
                shown = map != null && MenuFlag(menu, "IsNewGame") && MenuFlag(menu, "IsNotMultiplayerOrIsHost")
                    && !MenuFlag(menu, "IsExistingAsset") && !MenuFlag(menu, "IsWorldEditorMenu") && !MenuFlag(menu, "IsCreditIslandMenu");
            }
            catch { }
            if (!shown) { panel.gameObject.SetActive(false); return; }
            panel.gameObject.SetActive(true);
            int n = ActiveCount;
            panel._keyLabel.text = "Мир";
            panel._keyLabel.color = BaseMenu.Colors.Black;
            panel._label.text = n == 0 ? "ПОДРОБНЫЕ НАСТРОЙКИ" : "НАСТРОЙКИ (ИЗМЕНЕНО: " + n + ")";
            panel._label.color = BaseMenu.Colors.Black;
            panel._selectionImage.color = BaseMenu.Colors.Black;
            panel._selectionImage.sprite = IconKeys._UI_Big_Arrow_Right.Get64();
            panel._selectionImage.gameObject.SetActive(true);
            panel._button.interactable = true;
        }

        // ---------- применение: Lua-правила режима ----------
        private static void PresetPostfix(Asset_Gamemode __instance, Script script)
        {
            try
            {
                var table = script == null ? null : script.Globals["sv_Settings"] as Table;
                if (table == null) return;
                int n = 0;
                foreach (var o in All)
                {
                    if (!o.On || o.Lua == null) continue;
                    var cur = table.Get(o.Lua);
                    if (ReferenceEquals(cur, null) || cur.IsNil()) continue; // у этого режима такого правила нет
                    if (o.Bool) table[o.Lua] = o.Value >= 0.5f;
                    else table[o.Lua] = (double)(o.Step >= 1 ? Mathf.Round(o.Value) : o.Value);
                    n++;
                }
                if (n > 0) Plugin.Log.LogInfo("Настройки мира: в режиме «" + __instance.codename + "» заменено правил: " + n);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Настройки мира: правила режима не применены: " + e.Message); }
        }

        // ---------- применение: правила GeneralRules (цена юнитов) ----------
        private static float _applyRulesAt = -1f;

        public static void OnLevelLoaded()
        {
            CloseWindow();
            _applyRulesAt = Time.realtimeSinceStartup + 0.1f;
            _reapplyAt = Time.realtimeSinceStartup + 4f; // Lua режима мог дописать правила позже конца загрузки
        }

        private static float _reapplyAt = -1f;

        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (_applyRulesAt > 0 && now >= _applyRulesAt) { _applyRulesAt = -1f; ApplyRules(); }
            if (_reapplyAt > 0 && now >= _reapplyAt) { _reapplyAt = -1f; ApplyRules(); }
        }

        private static void SetRule(GeneralRules rules, string name, int value)
        {
            var t = typeof(GeneralRules);
            var p = t.GetProperty("Network" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(rules, value, null); return; }
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) f.SetValue(rules, value);
        }

        private static void ApplyRules()
        {
            try
            {
                var rules = GeneralRules.Main;
                if (rules == null) return;
                if (NetworkClient.active && !NetworkServer.active) return; // клиент: значения придут от хоста
                int n = 0;
                if (UBase.On) { SetRule(rules, "homeCrystalSpawnRequiredEnergyBase", (int)UBase.Value); n++; }
                if (UStep.On) { SetRule(rules, "homeCrystalSpawnRequiredEnergyFactor", (int)UStep.Value); n++; }
                if (UFree.On) { SetRule(rules, "homeCrystalSpawnRequiredEnergyBeginCount", (int)UFree.Value); n++; }
                if (n > 0) Plugin.Log.LogInfo("Настройки мира: правил энергии применено " + n);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Настройки мира: правила энергии не применены: " + e.Message); }
        }

        // ---------- энергия от кристаллов ----------
        private static FieldInfo _breweryNest;
        private static readonly HashSet<FireflyNest> Nests = new HashSet<FireflyNest>();

        private static void NestAdd(FireflyNest __instance) { if (__instance != null) Nests.Add(__instance); }
        private static void NestRemove(FireflyNest __instance) { Nests.Remove(__instance); }

        private static int CapturedCrystals(FireflyNest of)
        {
            int n = 0;
            var faction = of.faction;
            List<FireflyNest> dead = null;
            foreach (var nest in Nests)
            {
                if (nest == null) { if (dead == null) dead = new List<FireflyNest>(); dead.Add(nest); continue; }
                if (nest.isHome || !nest.doBrew || nest.faction != faction) continue;
                if (!nest.gameObject.activeInHierarchy) continue;
                n++;
            }
            if (dead != null) foreach (var d in dead) Nests.Remove(d);
            return n;
        }

        private static void EnergyPostfix(object __instance, GameObject go)
        {
            try
            {
                if (!EMul.On && !AnyCount()) return;
                if (!NetworkServer.active) return;
                var nest = _breweryNest.GetValue(__instance) as FireflyNest;
                var ff = go == null ? null : go.GetComponent<Firefly>();
                if (nest == null || ff == null) return;
                float k = EMul.On ? EMul.Value : 1f;
                int crystals = CapturedCrystals(nest);
                if (crystals > 0)
                {
                    var o = ECount[Math.Min(crystals, 6) - 1];
                    if (o.On) k *= o.Value;
                }
                if (Math.Abs(k - 1f) < 0.001f) return;
                ff.pureEnergy = Mathf.Max(1, Mathf.RoundToInt(ff.pureEnergy * k));
            }
            catch (Exception e) { LogOnce("энергия", e); }
        }

        private static bool AnyCount()
        {
            foreach (var o in ECount) if (o.On) return true;
            return false;
        }

        // ---------- возрождение погибших ----------
        private static MethodInfo _getFirefly, _consume, _spawn;

        /// <summary>Заменяет FeedQueuedFireflies (возрождение погибших), если задана цена возрождения. Логика — как в игре, цена своя.</summary>
        private static bool FeedPrefix(FireflyNest __instance)
        {
            if (!RMul.On && !RFlat.On) return true;
            try
            {
                if (_getFirefly == null || _consume == null || _spawn == null) return true;
                var rules = GeneralRules.Main;
                int num = __instance.GetActiveOrSpawnableCharacterCount();
                int energy = __instance.GetAvailablePurifiedEnergy();
                float mul = RMul.On ? RMul.Value : 1f;
                int flat = RFlat.On ? (int)RFlat.Value : 0;
                Func<Firefly, bool> cond = delegate (Firefly ff) { return ff.queued && !ff.IsSpawning && ff.captureTimer.Check(); };
                int guard = 0;
                while (guard++ < 50)
                {
                    int cost = Mathf.Max(0, Mathf.RoundToInt(rules.GetHomeCrystalSpawnRequiredEnergy(num) * mul) + flat);
                    if (energy < cost) break;
                    var ff = _getFirefly.Invoke(__instance, new object[] { cond }) as Firefly;
                    if (ff == null) break;
                    _consume.Invoke(__instance, new object[] { cost });
                    _spawn.Invoke(__instance, new object[] { ff });
                    energy -= cost;
                    num++;
                }
                return false;
            }
            catch (Exception e) { LogOnce("возрождение", e); return true; }
        }

        private static readonly HashSet<string> Logged = new HashSet<string>();
        private static void LogOnce(string what, Exception e)
        {
            if (Logged.Add(what)) Plugin.Log.LogWarning("Настройки мира (" + what + "): " + e.Message);
        }

        // ---------- сохранение ----------
        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split('|');
                    if (p.Length < 3) continue;
                    foreach (var o in All)
                    {
                        if (o.Id != p[0]) continue;
                        o.On = p[1] == "1";
                        float v;
                        if (float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) o.Value = Mathf.Clamp(v, o.Min, o.Max);
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Настройки мира: файл не прочитан: " + e.Message); }
        }

        private static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var o in All)
                    sb.Append(o.Id).Append('|').Append(o.On ? "1" : "0").Append('|').Append(o.Value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Настройки мира: файл не записан: " + e.Message); }
        }

        // ---------- окно ----------
        private static bool _open;
        private static Vector2 _scroll;
        private static bool _eventSystemWasOn;
        private static GUIStyle _title, _name, _hint, _group, _value;

        public static bool IsOpen { get { return _open; } }

        private static void Open()
        {
            _open = true;
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null) { _eventSystemWasOn = es.enabled; es.enabled = false; } // клики не должны проходить сквозь окно в меню
            }
            catch { }
        }

        private static void CloseWindow()
        {
            if (!_open) return;
            _open = false;
            Save();
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null) es.enabled = _eventSystemWasOn || es.enabled;
            }
            catch { }
        }

        private static void Styles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _name = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
            _hint = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.UpperLeft, wordWrap = true };
            _hint.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
            _group = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _group.normal.textColor = new Color(1f, 0.77f, 0.04f);
            _value = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
        }

        public static void DrawGUI()
        {
            if (!_open) return;
            Styles();
            float s = Mathf.Clamp(Screen.height / 900f, 0.8f, 2f);
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            float sw = Screen.width / s, sh = Screen.height / s;
            GUI.depth = -1000;
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(0, 0, sw, sh), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float w = Mathf.Min(760f, sw - 40f), h = Mathf.Min(sh - 40f, 820f);
            var win = new Rect((sw - w) / 2f, (sh - h) / 2f, w, h);
            GUI.Box(win, GUIContent.none);
            GUI.Box(win, GUIContent.none);

            GUI.Label(new Rect(win.x + 20, win.y + 8, w - 40, 36), "ПОДРОБНЫЕ НАСТРОЙКИ МИРА", _title);
            GUI.Label(new Rect(win.x + 20, win.y + 42, w - 40, 36),
                "Галочка слева включает свою настройку, иначе действует то, что задано в режиме. Значения хоста главные — у всех игроков должны быть одинаковые.", _hint);

            var view = new Rect(win.x + 10, win.y + 84, w - 20, h - 84 - 56);
            float rowH = 58f, groupH = 34f;
            float total = 0; string last = null;
            foreach (var o in All) { if (o.Group != last) { total += groupH; last = o.Group; } total += rowH; }
            _scroll = GUI.BeginScrollView(view, _scroll, new Rect(0, 0, view.width - 20, total));
            float y = 0; last = null;
            foreach (var o in All)
            {
                if (o.Group != last) { GUI.Label(new Rect(8, y + 4, view.width - 40, 26), o.Group.ToUpper(), _group); y += groupH; last = o.Group; }
                DrawRow(o, new Rect(8, y, view.width - 40, rowH));
                y += rowH;
            }
            GUI.EndScrollView();

            if (GUI.Button(new Rect(win.x + 20, win.yMax - 46, 190, 36), "Сбросить всё"))
                foreach (var o in All) { o.On = false; o.Value = o.Def; }
            if (GUI.Button(new Rect(win.xMax - 210, win.yMax - 46, 190, 36), "Готово")) CloseWindow();
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape) { CloseWindow(); Event.current.Use(); }
            GUI.matrix = old;
        }

        private static void DrawRow(Opt o, Rect r)
        {
            o.On = GUI.Toggle(new Rect(r.x, r.y + 4, 24, 24), o.On, GUIContent.none);
            GUI.Label(new Rect(r.x + 30, r.y, r.width * 0.46f, 30), o.Name, _name);
            bool en = GUI.enabled;
            GUI.enabled = o.On;
            float sx = r.x + 30 + r.width * 0.46f + 8, sw = r.width - (sx - r.x) - 110;
            if (o.Bool)
            {
                bool b = GUI.Toggle(new Rect(sx, r.y + 4, 120, 24), o.Value >= 0.5f, o.Value >= 0.5f ? "Да" : "Нет");
                o.Value = b ? 1f : 0f;
            }
            else
            {
                float v = GUI.HorizontalSlider(new Rect(sx, r.y + 10, sw, 16), o.Value, o.Min, o.Max);
                if (o.Step > 0) v = Mathf.Round(v / o.Step) * o.Step;
                o.Value = Mathf.Clamp(v, o.Min, o.Max);
                string text = o.Choices != null ? o.Choices[Mathf.Clamp((int)o.Value, 0, o.Choices.Length - 1)]
                    : (o.Step >= 1 ? ((int)o.Value).ToString() : o.Value.ToString(o.Step < 0.1f ? "0.00" : "0.0", CultureInfo.InvariantCulture));
                GUI.Label(new Rect(sx + sw + 6, r.y, 100, 30), text, _value);
            }
            GUI.enabled = en;
            string def = o.Bool ? (o.Def >= 0.5f ? "Да" : "Нет") : o.Choices != null ? o.Choices[(int)o.Def] : o.Def.ToString(o.Step >= 1 ? "0" : "0.##", CultureInfo.InvariantCulture);
            GUI.Label(new Rect(r.x + 30, r.y + 28, r.width - 30, 28), o.Hint + "  (в игре: " + def + ")", _hint);
        }
    }
}
