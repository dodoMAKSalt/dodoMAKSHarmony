using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
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
using UnityEngine.Networking.NetworkSystem;

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

        private static Opt Cap, Workers, Knights, Archers, Crew;
        private static Opt UBase, UStep, UFree, RMul, RFlat, EMul;
        private static readonly Opt[] ECount = new Opt[6];
        private static Opt StartTime, DayF, NightF, Pause, Relations, Dig;
        private static Opt OreHp, OreYield;
        private static Opt PumpFirst, Pump, ToWin, PerWave;

        private const string GUnits = "Юниты", GEnergy = "Энергия и цена юнитов", GCrystals = "Добыча энергии по числу кристаллов",
            GTime = "Время суток", GMining = "Добыча", GRules = "Правила", GGems = "Захват самоцветов";

        static WorldSettings()
        {
            Cap = Add(GUnits, "cap", "bricktronCap", "Макс. юнитов у каждого игрока", "Предел численности (в режимах обычно 12–15).", 1, 300, 1, 15);
            Workers = Add(GUnits, "workers", "startingWorkersCount", "Стартовые рабочие", "Сколько бриктронов у игрока в начале.", 0, 60, 1, 5);
            Knights = Add(GUnits, "knights", "startingKnightCount", "Стартовые рыцари", "Только Вторжение и Завоевание.", 0, 30, 1, 1);
            Archers = Add(GUnits, "archers", "startingArcherCount", "Стартовые лучники", "Только Вторжение и Завоевание.", 0, 30, 1, 1);

            Crew = Add(GUnits, "crew", null, "Предел бригады на одну работу", "Потолок для раздела «Бригада» в меню приказов и +/- в списке задач (в игре было 8; без галочки — 30). Число ячеек в списке задач меняется только с галочкой.", 1, 30, 1, 8);

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

            OreHp = Add(GMining, "orehp", null, "Прочность руды и камня (×)", "Во сколько раз дольше бить руду (железо, булыжник, кристаллы, бриллиты). Землю не трогает.", 0.25f, 10, 0.25f, 1);
            OreYield = Add(GMining, "oreyield", null, "Выход ресурсов с руды и камня (×)", "Во сколько раз больше (меньше) ресурса падает с добытого блока.", 0.25f, 10, 0.25f, 1);

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
                var hp = AccessTools.Method(typeof(Brix.Game.AI.Damage.TerrainDamageReceiver), "SetMaxHPAndOverkillFromFactory");
                h.Patch(hp, postfix: new HarmonyMethod(typeof(WorldSettings), nameof(HpPostfix)));
                var gv = AccessTools.Method(typeof(Brix.Engine.Voxel), "GetGarnotteValue",
                    new[] { typeof(Brix.Engine.XYZ), typeof(System.Collections.Generic.ICollection<Brix.External.Factories.Factory.AssetKey>), typeof(System.Collections.Generic.ICollection<int>) });
                h.Patch(gv, postfix: new HarmonyMethod(typeof(WorldSettings), nameof(YieldPostfix)));
            }
            catch (Exception e) { Plugin.Log.LogError("Настройки мира: прочность и выход руды не изменятся: " + e); }
            try
            {
                var setter = AccessTools.PropertySetter(typeof(Brix.Game.AI.Project), "MaximumCrewSize");
                h.Patch(setter, transpiler: new HarmonyMethod(typeof(WorldSettings), nameof(CrewTranspiler)));
                var load = typeof(Brix.Lua.BrixScriptLoader).GetMethod("MoonSharp.Interpreter.Loaders.IScriptLoader.LoadFile",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (load != null) h.Patch(load, postfix: new HarmonyMethod(typeof(WorldSettings), nameof(LuaFilePostfix)));
                else Plugin.Log.LogWarning("Настройки мира: загрузчик Lua не найден — размер бригады в списке задач не изменится.");
            }
            catch (Exception e) { Plugin.Log.LogError("Настройки мира: размер бригады не изменится: " + e); }
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
                // предел юнитов по фракциям: заменяем чтение GeneralRules.bricktronCap в двух местах игры
                _capGetter = AccessTools.PropertyGetter(typeof(GeneralRules), "bricktronCap");
                _capField = AccessTools.Field(typeof(GeneralRules), "_bricktronCap");
                _nestField = _breweryNest;
                var tr = new HarmonyMethod(typeof(WorldSettings), nameof(CapTranspiler));
                h.Patch(AccessTools.Method(brew, "CanBrew"), transpiler: tr);
                h.Patch(AccessTools.Method(nest, "SpawnNamedFireflies"), transpiler: tr);
                h.Patch(_capGetter, postfix: new HarmonyMethod(typeof(WorldSettings), nameof(CapGetterPostfix)));
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
                if (!(NetworkClient.active && !NetworkServer.active)) RebuildRuntime();
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
            if (!(NetworkClient.active && !NetworkServer.active)) RebuildRuntime();
            RegisterNetwork();
            if (NetworkServer.active && Brix.Engine.Neo.IsMultiplayer)
                try { NetworkServer.SendToAll(MsgCaps, new CapsMsg { Data = Pack() }); } catch { }
            _applyRulesAt = Time.realtimeSinceStartup + 0.1f;
            _reapplyAt = Time.realtimeSinceStartup + 4f; // Lua режима мог дописать правила позже конца загрузки
        }

        private static float _reapplyAt = -1f;

        public static void Tick()
        {
            if (!_open && _disabledEs != null) RestoreEventSystem(); // страховка: окно закрыто, а меню заблокировано
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

        // ---------- добыча: прочность и выход руды ----------
        private static bool IsOre(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            name = name.ToLowerInvariant();
            return name.Contains("iron") || name.Contains("rock") || name.Contains("stone") || name.Contains("crystal")
                || name.Contains("pyrite") || name.Contains("brick") || name.Contains("boulder");
        }

        private static void HpPostfix(Brix.Game.AI.Damage.TerrainDamageReceiver __instance, Brix.Engine.TerrainBlockData tbd)
        {
            try
            {
                if (!OreHp.On || Math.Abs(OreHp.Value - 1f) < 0.001f) return;
                if (!IsOre(tbd.BlockInfo.Key.Name)) return;
                __instance.MaxHP *= OreHp.Value;
                __instance.OverkillThreshold *= OreHp.Value;
            }
            catch (Exception e) { LogOnce("прочность", e); }
        }

        private static void YieldPostfix(Brix.Engine.XYZ position, System.Collections.Generic.ICollection<Brix.External.Factories.Factory.AssetKey> resultTypes, System.Collections.Generic.ICollection<int> resultAmounts)
        {
            try
            {
                if (!OreYield.On || Math.Abs(OreYield.Value - 1f) < 0.001f) return;
                var list = resultAmounts as System.Collections.Generic.IList<int>;
                if (list == null || !Brix.Engine.Voxel.IsTerrain(position)) return; // построенные блоки не трогаем
                var types = new System.Collections.Generic.List<Brix.External.Factories.Factory.AssetKey>(resultTypes);
                for (int i = 0; i < list.Count; i++)
                {
                    string name = i < types.Count ? types[i].Name : null;
                    if (IsOre(name)) list[i] = Mathf.Max(1, Mathf.RoundToInt(list[i] * OreYield.Value));
                }
            }
            catch (Exception e) { LogOnce("выход", e); }
        }

        // ---------- размер бригады на одну работу ----------
        public static int CrewLimit() { return Crew.On ? (int)Crew.Value : 30; }

        private static IEnumerable<CodeInstruction> CrewTranspiler(IEnumerable<CodeInstruction> code)
        {
            var ours = AccessTools.Method(typeof(WorldSettings), nameof(CrewLimit));
            int n = 0;
            foreach (var ins in code)
            {
                if (ins.opcode == OpCodes.Ldc_I4_8) { n++; yield return new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(ins); }
                else yield return ins;
            }
            if (n == 0) Plugin.Log.LogWarning("Настройки мира: в Project.MaximumCrewSize не нашли предел 8.");
        }

        /// <summary>Menu_TaskList.lua: MaxCrewMemberCount возвращает 8 — число ячеек бригады в списке задач.</summary>
        private static void LuaFilePostfix(string file, ref object __result)
        {
            try
            {
                if (!Crew.On || file == null || !file.EndsWith("Menu_TaskList.lua", StringComparison.OrdinalIgnoreCase)) return;
                var code = __result as string;
                if (code == null) return;
                var re = new System.Text.RegularExpressions.Regex(@"(archerId then return 30 end\s*return )8");
                if (re.IsMatch(code)) __result = re.Replace(code, "${1}" + (int)Crew.Value, 1);
                else Plugin.Log.LogWarning("Настройки мира: в Menu_TaskList.lua не нашли размер бригады.");
            }
            catch (Exception e) { LogOnce("lua-бригада", e); }
        }

        // ---------- предел юнитов по фракциям ----------
        internal sealed class FCap { public bool On; public float Value = 15; }
        private sealed class FRow { public string Name, Title; public Color Color; }

        private static readonly Dictionary<string, FCap> Saved = new Dictionary<string, FCap>();   // по имени фракции, хранится в файле
        private static Dictionary<string, int> Runtime = new Dictionary<string, int>();            // действующие пределы (хост: свои, клиент: от хоста)
        private static readonly List<FRow> Rows = new List<FRow>();
        private static MethodInfo _capGetter;
        private static FieldInfo _capField, _nestField;
        private const short MsgCaps = 31339, MsgReq = 31340;

        private static IEnumerable<CodeInstruction> CapTranspiler(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            var ours = AccessTools.Method(typeof(WorldSettings), nameof(CapFor));
            bool inBrewery = original.DeclaringType != typeof(FireflyNest);
            int n = 0;
            foreach (var ins in code)
            {
                if (ins.Calls(_capGetter))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    if (inBrewery) yield return new CodeInstruction(OpCodes.Ldfld, _nestField);
                    yield return new CodeInstruction(OpCodes.Call, ours);
                    n++;
                }
                else yield return ins;
            }
            if (n == 0) Plugin.Log.LogWarning("Настройки мира: в " + original.Name + " не нашли чтение предела юнитов.");
        }

        /// <summary>Предел юнитов для фракции этого гнезда: свой (по цвету), иначе общий из правил.</summary>
        public static int CapFor(GeneralRules rules, FireflyNest nest)
        {
            int cap = (int)_capField.GetValue(rules);
            try
            {
                int v;
                if (nest != null && nest.faction != null && Runtime.TryGetValue(nest.faction.name ?? "", out v)) return v;
            }
            catch { }
            return cap;
        }

        /// <summary>Lua (счётчик «юниты / предел», кнопка создания) читает Rules.bricktronCap — показываем предел своей фракции.</summary>
        private static void CapGetterPostfix(ref int __result)
        {
            try
            {
                if (Runtime.Count == 0) return;
                var u = User.LocalUser;
                int v;
                if (u != null && u.faction != null && Runtime.TryGetValue(u.faction.name ?? "", out v)) __result = v;
            }
            catch { }
        }

        private static void RebuildRuntime()
        {
            var d = new Dictionary<string, int>();
            foreach (var kv in Saved) if (kv.Value.On) d[kv.Key] = (int)Mathf.Round(kv.Value.Value);
            Runtime = d;
        }

        private class CapsMsg : MessageBase
        {
            public string Data = "";
            public override void Serialize(NetworkWriter w) { w.Write(Data ?? ""); }
            public override void Deserialize(NetworkReader r) { Data = r.ReadString(); }
        }

        private static string Pack()
        {
            var sb = new StringBuilder();
            foreach (var kv in Runtime) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            return sb.ToString();
        }

        private static void Unpack(string s)
        {
            var d = new Dictionary<string, int>();
            foreach (var part in (s ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                int v;
                if (kv.Length == 2 && int.TryParse(kv[1], out v)) d[kv[0]] = v;
            }
            Runtime = d;
        }

        private static void RegisterNetwork()
        {
            try
            {
                if (NetworkServer.active)
                {
                    NetworkServer.RegisterHandler(MsgReq, delegate (NetworkMessage m)
                    {
                        NetworkServer.SendToAll(MsgCaps, new CapsMsg { Data = Pack() });
                    });
                }
                var client = NetworkManager.singleton != null ? NetworkManager.singleton.client : null;
                if (client != null && !NetworkServer.active)
                {
                    client.RegisterHandler(MsgCaps, delegate (NetworkMessage m)
                    {
                        if (NetworkServer.active) return;
                        Unpack(m.ReadMessage<CapsMsg>().Data);
                    });
                    if (client.isConnected) client.Send(MsgReq, new EmptyMessage()); // попросить пределы у хоста
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Настройки мира: сеть: " + e.Message); }
        }

        /// <summary>Строки окна: фракции выбранного режима (цвет из режима, как флаги в лобби).</summary>
        private static void RefreshRows()
        {
            Rows.Clear();
            try
            {
                var mode = GameParam.Gamemode;
                if (mode == null) return;
                foreach (var f in mode.Disk_ReadFactionPrototypes())
                {
                    if (f.isAI || f.isDefault || f.isSpectator) continue;
                    uint c = f.colour;
                    var col = new Color(((c >> 24) & 255) / 255f, ((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, 1f);
                    Rows.Add(new FRow { Name = f.name, Title = ColorName(f.name), Color = col });
                    if (!Saved.ContainsKey(f.name)) Saved[f.name] = new FCap { Value = Cap.On ? Cap.Value : Cap.Def };
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Настройки мира: фракции режима не прочитаны: " + e.Message); }
        }

        private static string ColorName(string n)
        {
            switch ((n ?? "").ToLowerInvariant())
            {
                case "blue": return "Синий";
                case "red": return "Красный";
                case "green": return "Зелёный";
                case "yellow": return "Жёлтый";
                case "purple": return "Фиолетовый";
                case "orange": return "Оранжевый";
                case "pink": return "Розовый";
                case "cyan": return "Голубой";
                case "white": return "Белый";
                case "black": return "Чёрный";
                default: return n;
            }
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
                    if (p[0].StartsWith("fcap:"))
                    {
                        float fv;
                        float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out fv);
                        Saved[p[0].Substring(5)] = new FCap { On = p[1] == "1", Value = Mathf.Clamp(fv < 1 ? 15 : fv, 1, 300) };
                        continue;
                    }
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
                foreach (var kv in Saved)
                    sb.Append("fcap:").Append(kv.Key).Append('|').Append(kv.Value.On ? "1" : "0").Append('|').Append(kv.Value.Value.ToString("0", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Настройки мира: файл не записан: " + e.Message); }
        }

        // ---------- окно ----------
        private static bool _open;
        private static Vector2 _scroll;
        private static UnityEngine.EventSystems.EventSystem _disabledEs;

        // При отключении EventSystem Unity обнуляет EventSystem.current — включать обратно нужно сохранённый объект.
        private static void RestoreEventSystem()
        {
            var es = _disabledEs;
            _disabledEs = null;
            if (es != null && !es.enabled) es.enabled = true;
        }
        private static GUIStyle _title, _name, _hint, _group, _value;

        public static bool IsOpen { get { return _open; } }

        private static void Open()
        {
            _open = true;
            RefreshRows();
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null) { _disabledEs = es; es.enabled = false; } // клики не должны проходить сквозь окно в меню
            }
            catch { }
        }

        private static void CloseWindow()
        {
            if (!_open) return;
            _open = false;
            Save();
            if (!(NetworkClient.active && !NetworkServer.active)) RebuildRuntime();
            try
            {
                RestoreEventSystem();
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
            total += groupH + (Rows.Count == 0 ? rowH : Rows.Count * 40f);
            _scroll = GUI.BeginScrollView(view, _scroll, new Rect(0, 0, view.width - 20, total));
            float y = 0; last = null;
            foreach (var o in All)
            {
                if (o.Group != last) { GUI.Label(new Rect(8, y + 4, view.width - 40, 26), o.Group.ToUpper(), _group); y += groupH; last = o.Group; }
                DrawRow(o, new Rect(8, y, view.width - 40, rowH));
                y += rowH;
            }
            GUI.Label(new Rect(8, y + 4, view.width - 40, 26), "МАКС. ЮНИТОВ ПО ЦВЕТАМ ИГРОКОВ", _group); y += groupH;
            if (Rows.Count == 0) { GUI.Label(new Rect(8, y, view.width - 40, rowH), "Выбери карту и режим — здесь появятся цвета игроков.", _hint); y += rowH; }
            foreach (var fr in Rows)
            {
                var fc = Saved[fr.Name];
                fc.On = GUI.Toggle(new Rect(8, y + 6, 24, 24), fc.On, GUIContent.none);
                GUI.color = fr.Color;
                GUI.DrawTexture(new Rect(38, y + 6, 24, 24), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(68, y, view.width * 0.3f, 34), fr.Title, _name);
                bool en0 = GUI.enabled; GUI.enabled = fc.On;
                float fx = 68 + view.width * 0.3f + 8, fw = view.width - 40 - fx - 70;
                fc.Value = Mathf.Round(GUI.HorizontalSlider(new Rect(fx, y + 12, fw, 16), fc.Value, 1, 300));
                GUI.Label(new Rect(fx + fw + 6, y, 64, 34), ((int)fc.Value).ToString(), _value);
                GUI.enabled = en0;
                y += 40f;
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
