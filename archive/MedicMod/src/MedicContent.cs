using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Brix.External.Factories;
using Brix.External.Factories.Templates;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lua;
using Brix.UI.Icons;
using HarmonyLib;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using UnityEngine;

namespace CastleStoryOptimizer
{
    // ---------- Новые типы ресурсов (адъективы) ----------
    public class MedicHat : Outil
    {
        public static readonly AdjectiveKey Key = new AdjectiveKey("MedicHat");
        public override AdjectiveKey GetKey() { return Key; }
    }

    public class MedicBag : Outil
    {
        public static readonly AdjectiveKey Key = new AdjectiveKey("MedicBag");
        public override AdjectiveKey GetKey() { return Key; }
    }

    public class Bandage : Ressource
    {
        public static readonly AdjectiveKey Key = new AdjectiveKey("Bandage");
        public override AdjectiveKey GetKey() { return Key; }
    }

    public class MedicTool : CategorieOutil
    {
        public static readonly AdjectiveKey Key = new AdjectiveKey("MedicTool");
        public override AdjectiveKey GetKey() { return Key; }
    }

    /// <summary>
    /// Медики, этапы 1+2: предметы (шапка, сумка, бинт), рецепты, медицинский стол (строится из 10 досок и 1 железа),
    /// записи для Lua-интерфейса (окно стола, склады, меню строительства), место на оружейной стойке.
    /// Профессия и лечение — следующий этап.
    /// </summary>
    internal static class MedicContent
    {
        public const LuaCrafting.LuaRecipe RecipeHat = (LuaCrafting.LuaRecipe)100;
        public const LuaCrafting.LuaRecipe RecipeBag = (LuaCrafting.LuaRecipe)101;
        public const LuaCrafting.LuaRecipe RecipeBandage = (LuaCrafting.LuaRecipe)102;
        public const LuaCrafting.LuaResource ResHat = (LuaCrafting.LuaResource)100;
        public const LuaCrafting.LuaResource ResBag = (LuaCrafting.LuaResource)101;
        public const LuaCrafting.LuaResource ResBandage = (LuaCrafting.LuaResource)102;

        public static readonly MedicHat Hat = new MedicHat();
        public static readonly MedicBag Bag = new MedicBag();
        public static readonly Bandage BandageAdj = new Bandage();
        public static readonly MedicTool Tool = new MedicTool();

        public static Factory.AssetKey HatKey, BagKey, BandageKey, TableKey, TableBlueprintKey;
        public static RecipeInfo HatRecipe, BagRecipe, BandageRecipe;
        public static bool Registered;
        private static bool _failed, _luaMapped;
        private static Texture2D _palette, _flatNormal, _black;
        private static readonly Dictionary<string, Adjectif> ByName = new Dictionary<string, Adjectif>(StringComparer.OrdinalIgnoreCase)
        {
            { "MedicHat", Hat }, { "MedicBag", Bag }, { "Bandage", BandageAdj }, { "MedicTool", Tool }
        };

        // ---------- Патчи ----------
        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Adjectif), nameof(Adjectif.StringToAdjectif)),
                prefix: new HarmonyMethod(typeof(MedicContent), nameof(StringToAdjectifPrefix)));
            h.Patch(AccessTools.Method(typeof(LuaCrafting), nameof(LuaCrafting.Register)),
                postfix: new HarmonyMethod(typeof(MedicContent), nameof(LuaRegisterPostfix)));
            var loader = AccessTools.TypeByName("Brix.Lua.BrixScriptLoader");
            var load = loader == null ? null : AccessTools.GetDeclaredMethods(loader).FirstOrDefault(m => m.Name.EndsWith("LoadFile"));
            if (load != null) h.Patch(load, postfix: new HarmonyMethod(typeof(MedicContent), nameof(LuaFilePostfix)));
            else Plugin.Log.LogWarning("Медики: загрузчик Lua не найден — интерфейс стола не будет дописан.");
            // Регистрируемся ровно между «фабрики загружены» и «пулы объектов созданы»: тогда игра сама создаст пулы
            // и Lua-описания для наших предметов, как для своих.
            var pools = AccessTools.TypeByName("Brix.Lifecycle.Pooling.ObjectPoolSingleton");
            h.Patch(AccessTools.Method(pools, "GeneratePools"), prefix: new HarmonyMethod(typeof(MedicContent), nameof(GeneratePoolsPrefix)));
            MedicDiag.Apply(h);
            Plugin.Log.LogInfo("Медики: патчи установлены, предметы появятся после загрузки фабрик игры.");
        }

        private static bool StringToAdjectifPrefix(string adjectifString, ref Adjectif __result)
        {
            if (string.IsNullOrEmpty(adjectifString)) return true;
            var name = adjectifString.Split('.').Last();
            Adjectif a;
            if (!ByName.TryGetValue(name, out a)) return true;
            __result = a;
            return false;
        }

        private static void LuaRegisterPostfix() { MapLua(); }

        public static void Tick() { }

        private static void GeneratePoolsPrefix()
        {
            if (Registered || _failed) return;
            try
            {
                Register();
                Registered = true;
                MapLua();
                Plugin.Log.LogInfo("Медики: предметы, рецепты и медицинский стол добавлены.");
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogError("Медики: не удалось добавить контент: " + e);
            }
        }

        // ---------- Регистрация ----------
        private static void Register()
        {
            LoadTextures();

            // 1) типы ресурсов: словарь игры тип → экземпляр
            var defaults = AccessTools.Field(typeof(Adjectif), "defaultAdjectives").GetValue(null) as Dictionary<Type, Adjectif>;
            if (defaults != null)
                foreach (var a in ByName.Values) defaults[a.GetType()] = a;

            // 2) «паспорта» (имя/иконка) — копии паспортов бомбера
            Step("паспорт шапки", () => AddInfo("AlchemistHelmet", "MedicHat", "Шапка медика"));
            Step("паспорт сумки", () => AddInfo("AlchemistPouch", "MedicBag", "Сумка медика"));
            Step("паспорт бинта", () => AddInfo("Bomb", "Bandage", "Бинт"));
            Step("паспорт медика", () => AddInfo("AlchemistTool", "MedicTool", "Медик"));

            // 3) предметы
            HatKey = CloneItem(Accessories.Bonnet, "MedicHat", "medic_hat",
                new Adjectif[] { Tool, Make(typeof(MedicHat), 1), Make(typeof(Outil), 1) });
            BagKey = CloneItem(Accessories.Backpack, "MedicBag", "medic_bag",
                new Adjectif[] { Make(typeof(VisionDistance), 25), Tool, Make(typeof(MedicBag), 1), Make(typeof(Outil), 1) });
            BandageKey = CloneItem(new Factory.AssetKey("BuildResources", "Bomb"), "Bandage", "bandage",
                new Adjectif[] { Make(typeof(Bandage), 1), Make(typeof(Encombrement), 1) });

            // что видно в руках при переноске
            var props = AccessTools.Field(typeof(Props), "props").GetValue(null) as Dictionary<Ressource, Func<Recepteur, Factory.AssetKey>>;
            if (props != null)
            {
                props[Hat] = r => Accessories.HeldBonnet;
                props[Bag] = r => Accessories.HeldBackpack;
                props[BandageAdj] = r => Accessories.Bag;
            }

            // 4) рецепты
            HatRecipe = new RecipeMaker { Work = 10, Heat = 0, Creates = HatKey, QteCreated = 1, Icon = IconKeys._Healing_Ward, Drawing = IconKeys._Healing_Ward }
                .With(Adjectif.plant, 50).Make();
            BagRecipe = new RecipeMaker { Work = 15, Heat = 0, Creates = BagKey, QteCreated = 1, Icon = IconKeys._Healing_Ward, Drawing = IconKeys._Healing_Ward }
                .With(Adjectif.rope, 2).With(Adjectif.plant, 30).Make();
            BandageRecipe = new RecipeMaker { Work = 5, Heat = 0, Creates = BandageKey, QteCreated = 1, Icon = IconKeys._Healing_Ward, Drawing = IconKeys._Healing_Ward }
                .With(Adjectif.plant, 10).Make();

            // 5) медицинский стол и его чертёж
            TableKey = CloneStation();
            TableBlueprintKey = CloneBlueprint();

            // 6) оружейная стойка принимает шапку и сумку медика
            Step("оружейная стойка", SetupToolRack);
        }

        /// <summary>Копия шаблона фабрики: CloneTemplate — через Factory.Instantiate (как Lua-моды игры),
        /// PeekTemplate и прочие — обычным Unity-клоном (для них Instantiate запрещён).</summary>
        private static GameObject Spawn(Factory.AssetKey src)
        {
            // Только обычный Unity-клон: Factory.Instantiate до создания пулов ломает загрузку игры.
            var go = UnityEngine.Object.Instantiate(Factory.Peek(src));
            go.SetActive(false);
            return go;
        }

        /// <summary>Если Lua-реестр игры уже собран — добавляем описание сами; иначе он подхватит наш шаблон при сборке.</summary>
        private static void RegisterDriver(LuaDriver d)
        {
            if (d == null) return;
            var f = AccessTools.Field(typeof(LuaDriverHQ), "_allStatics");
            if (f == null || f.GetValue(null) == null) return;
            try { LuaDriverHQ.AddNewDriver(d, fromFactory: true); }
            catch (Exception e) { Plugin.Log.LogWarning("Медики: Lua-описание " + d.Handle + ": " + e.Message); }
        }

        /// <summary>Добавить объект в ту же фабрику и с тем же типом шаблона, что у исходного.</summary>
        private static Factory.AssetKey AddLike(Factory.AssetKey src, GameObject go)
        {
            var f = Factory.GetFactory(src.Factory);
            var tplType = f.GetTemplate(src).GetType();
            // «Отпечаток» фабрики хранит ключ ОРИГИНАЛА — без замены игра видит два одинаковых шаблона.
            var newKey = new Factory.AssetKey(src.Factory, go.name);
            foreach (var imp in go.GetComponents<Brix.External.Factories.FactoryImprint>())
                AccessTools.Field(typeof(Brix.External.Factories.FactoryImprint), "_assetKey").SetValue(imp, newKey);
            var m = typeof(Factory).GetMethod("AddAsset").MakeGenericMethod(tplType);
            var key = (Factory.AssetKey)m.Invoke(f, new object[] { go });
            // Шаблоны игры при загрузке получают «кэш компонентов» (_gcPoco); у Unity-клона его нет, и чтение
            // AssetKey/Blueprint у копии падает (ломалась привязка чертежей → отмена загрузки сохранения).
            // Как Factory.InitializeGO у игры: Initialize() компонентов корня — вычисляет «главный ресурс» предмета
            // (Apparence._principale), кэши и т.п. Без этого окно рецептов стола падало и загрузка отменялась.
            foreach (var ini in go.GetComponents<Brix.External.Factories.IInitializable>())
            {
                try { ini.Initialize(); }
                catch (Exception e) { Plugin.Log.LogDebug("Медики: Initialize " + ini.GetType().Name + " у " + go.name + ": " + e.Message); }
            }
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                var gcl = c.GetType().GetMethod("GetCompCacheList", BindingFlags.Instance | BindingFlags.Public);
                if (gcl == null || gcl.GetParameters().Length != 0) continue;
                try { gcl.Invoke(c, null); } catch { }
            }
            return key;
        }

        private static Adjectif Make(Type t, int v) { return Adjectif.New(t, v); }

        /// <summary>Необязательный шаг: ошибка пишется в лог, остальное продолжается.</summary>
        private static void Step(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Plugin.Log.LogWarning("Медики: шаг «" + what + "» не удался: " + e.Message); }
        }

        private static void AddInfo(string fromCode, string code, string name)
        {
            var src = new Factory.AssetKey("Adjectives", fromCode);
            var dstKey = new Factory.AssetKey("Adjectives", code);
            if (Factory.Exist(dstKey)) return;
            // «Паспорта» — PeekTemplate: Factory.Instantiate для них запрещён, копируем обычным Unity-клоном.
            var go = UnityEngine.Object.Instantiate(Factory.Peek(src));
            go.SetActive(false);
            go.name = code;
            var info = go.GetComponent<AdjectiveInfo>();
            info.codeName = code;
            info.descriptiveName = name;
            AddLike(src, go);
            var peek = Factory.Peek(dstKey).GetComponent<AdjectiveInfo>();
            var all = AccessTools.Field(typeof(AdjectiveInfo), "_allInfos").GetValue(null) as Dictionary<string, AdjectiveInfo>;
            if (all != null) all[code] = peek;
            var byType = AccessTools.Field(typeof(AdjectiveInfo), "_typeInfo").GetValue(null) as Dictionary<AdjectiveInfo.AdjectiveType, List<AdjectiveInfo>>;
            if (byType != null && peek.type != AdjectiveInfo.AdjectiveType.Adjectif)
            {
                List<AdjectiveInfo> l;
                if (!byType.TryGetValue(peek.type, out l)) byType[peek.type] = l = new List<AdjectiveInfo>();
                l.Add(peek);
            }
        }

        // ---------- Клонирование предметов ----------
        private static Factory.AssetKey CloneItem(Factory.AssetKey src, string name, string model, Adjectif[] desc)
        {
            var dst = new Factory.AssetKey(src.Factory, name);
            if (Factory.Exist(dst)) return dst;
            var go = Spawn(src);
            go.name = name;
            var ld = go.GetComponent<LuaDriver>();
            if (ld != null) ld.Handle = "fy_" + name;

            var ap = go.GetComponent<Apparence>();
            if (ap != null) UnityEngine.Object.DestroyImmediate(ap);
            ap = go.AddComponent<Apparence>();
            foreach (var a in desc) ap.description.Add(a.Clone());

            var mesh = LoadObj(model);
            ReplaceVisuals(go, mesh, name);

            var key = AddLike(src, go);
            var tld = Factory.Peek(key).GetComponent<LuaDriver>();
            RegisterDriver(tld);
            var apk = Factory.Peek(key).GetComponent<Apparence>();
            if (apk == null || apk.Principale == null) throw new Exception("у предмета " + key + " не определился главный ресурс");
            Plugin.Log.LogInfo("Медики: предмет " + key + " (главный ресурс " + apk.Principale.GetType().Name + ")");
            return key;
        }

        private static readonly Type RoiType = AccessTools.TypeByName("Brix.Game.Factories.RequestOnInitialize");

        /// <summary>Модель: у надеваемых вещей она подгружается из фабрики визуалов (RequestOnInitialize.key) —
        /// делаем копию визуала с нашей моделью и подменяем ключ. Прямые MeshFilter тоже подменяем.</summary>
        private static void ReplaceVisuals(GameObject go, Mesh mesh, string tag)
        {
            bool done = false;
            if (RoiType != null)
            {
                var keyField = AccessTools.Field(RoiType, "key");
                foreach (var roi in go.GetComponentsInChildren(RoiType, true))
                {
                    var vkey = (Factory.AssetKey)keyField.GetValue(roi);
                    if (Factory.AssetKey.IsNullOrEmpty(vkey) || !Factory.Exist(vkey)) continue;
                    var newKey = new Factory.AssetKey(vkey.Factory, vkey.Name + "_dodo_" + tag);
                    if (!Factory.Exist(newKey))
                    {
                        var v = Spawn(vkey);
                        v.name = newKey.Name;
                        ReplaceMeshes(v, mesh, true);
                        AddLike(vkey, v);
                    }
                    keyField.SetValue(roi, newKey);
                    done = true;
                }
            }
            if (go.GetComponentsInChildren<MeshFilter>(true).Length > 0) { ReplaceMeshes(go, mesh, true); done = true; }
            if (!done) Plugin.Log.LogWarning("Медики: у " + go.name + " не найдено модели для замены");
        }

        /// <summary>Главный (самый большой) меш заменяем нашим, подогнав по размеру/осям; остальные прячем.</summary>
        private static void ReplaceMeshes(GameObject root, Mesh mesh, bool replaceMaterial, params string[] keepNames)
        {
            var filters = root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).ToList();
            if (filters.Count == 0) return;
            var main = filters.OrderByDescending(f => { var s = f.sharedMesh.bounds.size; return s.x * s.y * s.z; }).First();
            var fitted = FitUpright(mesh, main.sharedMesh.bounds, root.transform, main.transform);
            fitted.name = mesh.name;
            main.sharedMesh = fitted;
            var mr = main.GetComponent<MeshRenderer>();
            if (mr != null && replaceMaterial && mr.sharedMaterial != null) mr.sharedMaterial = MakeMaterial(mr.sharedMaterial);
            foreach (var f in filters)
            {
                if (f == main || keepNames.Any(k => f.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                var r = f.GetComponent<Renderer>();
                if (r != null) r.enabled = false;
            }
        }

        private static Material MakeMaterial(Material src)
        {
            var m = new Material(src) { name = src.name + "_dodoMedic" };
            foreach (var p in new[] { "_MainTex", "_Diffuse", "_Albedo" }) if (m.HasProperty(p)) m.SetTexture(p, _palette);
            foreach (var p in new[] { "_Normals", "_BumpMap" }) if (m.HasProperty(p)) m.SetTexture(p, _flatNormal);
            foreach (var p in new[] { "_Specular", "_Emission", "_Distance" }) if (m.HasProperty(p)) m.SetTexture(p, _black);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            return m;
        }

        // ---------- Стол ----------
        private static Factory.AssetKey CloneStation()
        {
            var src = new Factory.AssetKey("ObjetsDynamiques", "Workbench");
            var dst = new Factory.AssetKey("ObjetsDynamiques", "MedicalTable");
            if (Factory.Exist(dst)) return dst;
            var go = Spawn(src);
            go.name = "MedicalTable";
            var ld = go.GetComponent<LuaDriver>();
            if (ld != null) ld.Handle = "fy_MedicalTable";
            ReplaceMeshes(go, LoadObj("medical_table"), true, "Hammer", "Crystal", "Lantern");
            var key = AddLike(src, go);
            var tld = Factory.Peek(key).GetComponent<LuaDriver>();
            RegisterDriver(tld);
            Plugin.Log.LogInfo("Медики: стол " + key);
            return key;
        }

        private static Factory.AssetKey CloneBlueprint()
        {
            var src = new Factory.AssetKey("Blueprints", "Workbench");
            var dst = new Factory.AssetKey("Blueprints", "MedicalTable");
            if (Factory.Exist(dst)) return dst;
            var go = Spawn(src);
            go.name = "MedicalTable";
            var ld = go.GetComponent<LuaDriver>();
            if (ld != null) ld.Handle = "fy_MedicalTableBlueprint";
            var bp = go.GetComponent<Blueprint>();
            bp.BlueprintResult = TableKey;
            var rec = go.GetComponent<Recepteur>();
            var cost = new Description();
            cost.Add(Adjectif.New(typeof(PlankBlock), 10));
            cost.Add(Adjectif.New(typeof(Iron), 1));
            AccessTools.Field(typeof(Recepteur), "_baseCapacity").SetValue(rec, cost);
            ReplaceMeshes(go, LoadObj("medical_table"), false, "Hammer");
            var key = AddLike(src, go);
            var tld = Factory.Peek(key).GetComponent<LuaDriver>();
            RegisterDriver(tld);
            Plugin.Log.LogInfo("Медики: чертёж стола " + key + " (10 досок + 1 железо)");
            return key;
        }

        // ---------- Оружейная стойка ----------
        private static void SetupToolRack()
        {
            var t = typeof(ToolRack);
            Func<string, List<Ressource>> L = n => AccessTools.Field(t, n).GetValue(null) as List<Ressource>;
            var hats = L("Hats"); var props = L("Props"); var all = L("AllItems");
            if (hats != null && !hats.Contains(Hat)) hats.Add(Hat);
            if (props != null && !props.Contains(Bag)) props.Add(Bag);
            if (all != null) { if (!all.Contains(Hat)) all.Add(Hat); if (!all.Contains(Bag)) all.Add(Bag); }
            var medic = new List<Ressource> { Bag, Hat };
            var classes = AccessTools.Field(t, "AllClasses").GetValue(null) as List<List<Ressource>>;
            if (classes != null) classes.Add(medic);
            var excl = AccessTools.Field(t, "Exclusivity").GetValue(null) as Dictionary<CategorieOutil, List<Ressource>>;
            if (excl != null) excl[Tool] = medic;

            var rackKey = new Factory.AssetKey("ObjetsDynamiques", "Toolrack");
            if (!Factory.Exist(rackKey)) return;
            var rec = Factory.Peek(rackKey).GetComponent<Recepteur>();
            var cap = AccessTools.Field(typeof(Recepteur), "_baseCapacity").GetValue(rec) as Description;
            if (cap != null)
            {
                if (!cap.Has(typeof(MedicHat))) cap.Add(Adjectif.New(typeof(MedicHat), 4));
                if (!cap.Has(typeof(MedicBag))) cap.Add(Adjectif.New(typeof(MedicBag), 4));
            }
        }

        // ---------- Lua: рецепты/ресурсы и таблицы интерфейса ----------
        private static void MapLua()
        {
            if (_luaMapped || !Registered) return;
            var isReg = AccessTools.Field(typeof(LuaCrafting), "_isRegistered");
            if (isReg == null || !(bool)isReg.GetValue(null)) return;
            try
            {
                var r2i = AccessTools.Field(typeof(LuaCrafting), "_luaRecipeToRecipeInfo").GetValue(null) as Dictionary<LuaCrafting.LuaRecipe, List<RecipeInfo>>;
                var i2r = AccessTools.Field(typeof(LuaCrafting), "_recipeInfoToLuaRecipe").GetValue(null) as Dictionary<RecipeInfo, LuaCrafting.LuaRecipe>;
                r2i[RecipeHat] = new List<RecipeInfo> { HatRecipe }; i2r[HatRecipe] = RecipeHat;
                r2i[RecipeBag] = new List<RecipeInfo> { BagRecipe }; i2r[BagRecipe] = RecipeBag;
                r2i[RecipeBandage] = new List<RecipeInfo> { BandageRecipe }; i2r[BandageRecipe] = RecipeBandage;

                var res2a = AccessTools.Field(typeof(LuaCrafting), "_luaResourceToResource").GetValue(null) as Dictionary<LuaCrafting.LuaResource, AdjectiveKey>;
                var a2res = AccessTools.Field(typeof(LuaCrafting), "_adjectiveToLuaResource").GetValue(null) as Dictionary<AdjectiveKey, LuaCrafting.LuaResource>;
                res2a[ResHat] = MedicHat.Key; a2res[MedicHat.Key] = ResHat;
                res2a[ResBag] = MedicBag.Key; a2res[MedicBag.Key] = ResBag;
                res2a[ResBandage] = Bandage.Key; a2res[Bandage.Key] = ResBandage;

                AddEnum<LuaCrafting.LuaRecipe>("RecipeEnum", "MedicHat", RecipeHat);
                AddEnum<LuaCrafting.LuaRecipe>("RecipeEnum", "MedicBag", RecipeBag);
                AddEnum<LuaCrafting.LuaRecipe>("RecipeEnum", "Bandage", RecipeBandage);
                AddEnum<LuaCrafting.LuaResource>("ResourceEnum", "MedicHat", ResHat);
                AddEnum<LuaCrafting.LuaResource>("ResourceEnum", "MedicBag", ResBag);
                AddEnum<LuaCrafting.LuaResource>("ResourceEnum", "Bandage", ResBandage);
                _luaMapped = true;
                Plugin.Log.LogInfo("Медики: рецепты и ресурсы добавлены в Lua.");
            }
            catch (Exception e) { Plugin.Log.LogError("Медики: Lua-рецепты: " + e); }
        }

        private static void AddEnum<T>(string field, string name, T value) where T : struct
        {
            var dv = (DynValue)AccessTools.Field(typeof(LuaCrafting), field).GetValue(null);
            var table = dv.Table;
            var sample = table.Pairs.Select(p => p.Value).FirstOrDefault(v => v.Type == DataType.UserData);
            var desc = sample.UserData.Descriptor;
            table.Set(name, UserData.CreateWithDescriptor(new LuaEnumProxy<T>(value), desc));
        }

        private static void LuaFilePostfix(string file, ref object __result)
        {
            if (!Registered || file == null) return;
            var code = __result as string;
            if (code == null) return;
            string add = null;
            var f = file.Replace('\\', '/');
            const string ico = "||IconKeys._Healing_Ward:Get64()";
            if (f.EndsWith("Meta/Meta_Recipe.lua"))
                add = "if Recipe.MedicHat then\n" +
                      "_t:Add(Recipe.MedicHat, {Name = ||\"Шапка медика\", Info = ||\"Часть набора медика: шапка. Наденьте вместе с сумкой медика.\", Icon = " + ico + "})\n" +
                      "_t:Add(Recipe.MedicBag, {Name = ||\"Сумка медика\", Info = ||\"Часть набора медика: сумка для бинтов (до 10).\", Icon = " + ico + "})\n" +
                      "_t:Add(Recipe.Bandage, {Name = ||\"Бинт\", Info = ||\"Медик перевязывает раненого за 7 с: +50% здоровья.\", Icon = " + ico + "})\nend\n";
            else if (f.EndsWith("Meta/Meta_Resource.lua"))
                add = "if Resource.MedicHat then\n" +
                      "_t:Add(Resource.MedicHat, {Name = ||\"Шапка медика\", Info = ||\"Часть набора медика.\", Icon = " + ico + ", StockpiledIcon = " + ico + "})\n" +
                      "_t:Add(Resource.MedicBag, {Name = ||\"Сумка медика\", Info = ||\"Часть набора медика.\", Icon = " + ico + ", StockpiledIcon = " + ico + "})\n" +
                      "_t:Add(Resource.Bandage, {Name = ||\"Бинт\", Info = ||\"Для перевязки раненых.\", Icon = " + ico + ", StockpiledIcon = " + ico + "})\nend\n";
            else if (f.EndsWith("Meta/Meta_Operable.lua"))
                add = "if fy_MedicalTable and Recipe.MedicHat then\n" +
                      "_t:Add(fy_MedicalTable, {Icon = " + ico + ", Name = ||\"Медицинский стол\", Info = ||\"Здесь делают набор медика и бинты.\",\n" +
                      "  Recipes = { Recipe.MedicHat, Recipe.MedicBag, Recipe.Bandage }})\nend\n";
            else if (f.EndsWith("Meta/Meta_Structure.lua"))
                add = "_t.Add(AssetKey.New(\"Blueprints\", \"MedicalTable\"), { Name = ||\"Медицинский стол\", Icon = " + ico + ", Hotkey = \"\", groupId = 3 })\n";
            if (add == null) return;
            int i = code.LastIndexOf("return _t", StringComparison.Ordinal);
            if (i < 0) return;
            __result = code.Substring(0, i) + "-- dodoMAKSharmony: медики\n" + add + "\n" + code.Substring(i);
            Plugin.Log.LogInfo("Медики: дописан " + System.IO.Path.GetFileName(f));
        }

        // ---------- Модели ----------
        private static void LoadTextures()
        {
            _palette = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "dodo_medic_palette" };
            _palette.LoadImage(ReadResource("medic/medic_palette.png"));
            _flatNormal = Solid(new Color(0.5f, 0.5f, 1f, 1f), "dodo_flat_normal");
            _black = Solid(Color.black, "dodo_black");
        }

        private static Texture2D Solid(Color c, string name)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = name };
            var px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = c;
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static byte[] ReadResource(string name)
        {
            using (var s = typeof(MedicContent).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) throw new FileNotFoundException("Нет ресурса " + name);
                var ms = new MemoryStream();
                var buf = new byte[8192];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }

        private static readonly Dictionary<string, Mesh> MeshCache = new Dictionary<string, Mesh>();

        /// <summary>Простой загрузчик .obj (v/vt/vn/f). OBJ правосторонний → для Unity отражаем X и меняем обход.</summary>
        private static Mesh LoadObj(string name)
        {
            Mesh cached;
            if (MeshCache.TryGetValue(name, out cached)) return cached;
            var text = System.Text.Encoding.ASCII.GetString(ReadResource("medic/" + name + ".obj"));
            var v = new List<Vector3>(); var vt = new List<Vector2>(); var vn = new List<Vector3>();
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var norms = new List<Vector3>(); var tris = new List<int>();
            var ci = CultureInfo.InvariantCulture;
            foreach (var raw in text.Split('\n'))
            {
                var p = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                if (p[0] == "v") v.Add(new Vector3(-float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci)));
                else if (p[0] == "vt") vt.Add(new Vector2(float.Parse(p[1], ci), float.Parse(p[2], ci)));
                else if (p[0] == "vn") vn.Add(new Vector3(-float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci)));
                else if (p[0] == "f")
                {
                    int start = verts.Count;
                    for (int k = 1; k < p.Length; k++)
                    {
                        var c = p[k].Split('/');
                        verts.Add(v[int.Parse(c[0]) - 1]);
                        uvs.Add(c.Length > 1 && c[1] != "" ? vt[int.Parse(c[1]) - 1] : Vector2.zero);
                        norms.Add(c.Length > 2 ? vn[int.Parse(c[2]) - 1] : Vector3.up);
                    }
                    for (int k = 1; k < p.Length - 2; k++) { tris.Add(start); tris.Add(start + k + 1); tris.Add(start + k); } // обход наоборот
                }
            }
            var m = new Mesh { name = "dodo_" + name };
            m.SetVertices(verts); m.SetUVs(0, uvs); m.SetNormals(norms); m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            MeshCache[name] = m;
            return m;
        }

        /// <summary>Подгоняем нашу модель под оригинальную: оси сопоставляем по величине (длинная к длинной),
        /// масштаб — по самой длинной стороне, центр — в центр оригинала.</summary>
        private static Mesh Fit(Mesh src, Bounds target)
        {
            var b = src.bounds;
            int[] ours = { 0, 1, 2 }, theirs = { 0, 1, 2 };
            Array.Sort(ours, (x, y) => b.size[y].CompareTo(b.size[x]));
            Array.Sort(theirs, (x, y) => target.size[y].CompareTo(target.size[x]));
            var perm = new int[3]; // наша ось → их ось
            for (int i = 0; i < 3; i++) perm[ours[i]] = theirs[i];
            float s = target.size[theirs[0]] / Mathf.Max(1e-4f, b.size[ours[0]]);
            bool odd = Parity(perm);
            var sv = src.vertices; var sn = src.normals;
            var nv = new Vector3[sv.Length]; var nn = new Vector3[sn.Length];
            for (int i = 0; i < sv.Length; i++)
            {
                var d = sv[i] - b.center;
                var o = Vector3.zero; var on = Vector3.zero;
                for (int a = 0; a < 3; a++) { o[perm[a]] = d[a] * s; if (i < sn.Length) on[perm[a]] = sn[i][a]; }
                nv[i] = o + target.center;
                if (i < sn.Length) nn[i] = on;
            }
            var tris = src.triangles;
            if (odd) for (int i = 0; i < tris.Length; i += 3) { var t = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = t; }
            var m = new Mesh();
            m.vertices = nv; m.normals = nn; m.uv = src.uv; m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Подгонка «стоя»: считаем габариты оригинала в системе КОРНЯ объекта (у мешей игры бывают повороты
        /// внутри префаба), наша модель остаётся вертикальной (ось Y вверх), по горизонтали при необходимости
        /// поворачивается на 90°, масштаб — по длинной стороне основания, низ — на уровень низа оригинала.</summary>
        private static Mesh FitUpright(Mesh src, Bounds childBounds, Transform root, Transform child)
        {
            var toRoot = root.worldToLocalMatrix * child.localToWorldMatrix;
            var toChild = toRoot.inverse;
            // габариты оригинала в системе корня
            Vector3 mn = Vector3.one * float.MaxValue, mx = Vector3.one * float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = childBounds.center + Vector3.Scale(childBounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var w = toRoot.MultiplyPoint3x4(c);
                mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
            }
            var t = new Bounds((mn + mx) * 0.5f, mx - mn);
            var b = src.bounds;
            bool rotate = (b.size.x >= b.size.z) != (t.size.x >= t.size.z);
            float ourLong = Mathf.Max(b.size.x, b.size.z), theirLong = Mathf.Max(t.size.x, t.size.z);
            float s = theirLong / Mathf.Max(1e-4f, ourLong);
            var sv = src.vertices; var sn = src.normals;
            var nv = new Vector3[sv.Length]; var nn = new Vector3[sn.Length];
            for (int i = 0; i < sv.Length; i++)
            {
                var d = sv[i] - new Vector3(b.center.x, b.min.y, b.center.z);
                if (rotate) d = new Vector3(-d.z, d.y, d.x);
                var r = new Vector3(t.center.x, t.min.y, t.center.z) + d * s; // в системе корня
                nv[i] = toChild.MultiplyPoint3x4(r);
                if (i < sn.Length)
                {
                    var n = sn[i];
                    if (rotate) n = new Vector3(-n.z, n.y, n.x);
                    nn[i] = toChild.MultiplyVector(n).normalized;
                }
            }
            var tris = src.triangles;
            // отражение в матрице (отрицательный масштаб у меша в префабе) — меняем обход граней
            if (toChild.determinant < 0)
                for (int i = 0; i < tris.Length; i += 3) { var tmp = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = tmp; }
            var m = new Mesh();
            m.vertices = nv; m.normals = nn; m.uv = src.uv; m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        private static bool Parity(int[] p)
        {
            int inv = 0;
            for (int i = 0; i < 3; i++) for (int j = i + 1; j < 3; j++) if (p[i] > p[j]) inv++;
            return inv % 2 == 1;
        }
    }
}
