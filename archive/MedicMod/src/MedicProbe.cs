using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lua;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Разведка для медиков: один раз после загрузки катки пишет в BepInEx/dodo_medic_probe.txt, как устроены изнутри
    /// шлем, сумка/шапка бомбера, бомба, верстак и его чертёж, «паспорта» ресурсов и рецепт набора бомбера.
    /// Ничего в игре не меняет — только читает.
    /// </summary>
    internal static class MedicProbe
    {
        private static bool _done;

        public static void Run()
        {
            if (_done) return;
            _done = true;
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("Фабрики: " + string.Join(", ", Factory.GetFactories().Select(f => f.name).ToArray()));
                string[] keys =
                {
                    "Accessories.Helmet", "Accessories.HeldHelmet", "Accessories.Bag", "Accessories.Pouch", "Accessories.Kepi",
                    "Accessories.Backpack", "Accessories.HeldBackpack", "Accessories.Bonnet",
                    "BuildResources.Bomb", "ObjetsDynamiques.Workbench", "Blueprints.Workbench", "ObjetsDynamiques.Toolrack",
                    "Adjectives.Bomb", "Adjectives.AlchemistPouch", "Adjectives.AlchemistHelmet", "Adjectives.Helmet", "Adjectives.AlchemistTool"
                };
                foreach (var k in keys) Dump(sb, new Factory.AssetKey(k));

                // Кто из шаблонов носит адъективы бомбера / шлема / бомбы
                var wanted = new[] { "AlchemistPouch", "AlchemistHelmet", "AlchemistTool", "AlchemistWepon", "Helmet", "Bomb", "Bandage", "HealingWardAdj" };
                sb.AppendLine("\n=== Шаблоны с нужными адъективами ===");
                foreach (var f in Factory.GetFactories())
                    foreach (var key in f.AssetKeys.ToList())
                    {
                        GameObject go;
                        try { go = Factory.Peek(key); } catch { continue; }
                        var ap = go == null ? null : go.GetComponent<Apparence>();
                        if (ap == null || ap.description == null) continue;
                        var names = ap.description.DicoAdjectif.Values.Select(a => a.GetType().Name).ToArray();
                        if (names.Any(n => wanted.Contains(n))) sb.AppendLine("  " + key + " : " + string.Join(", ", names));
                    }

                // Рецепт набора бомбера и сумки
                sb.AppendLine("\n=== Рецепты ===");
                foreach (var r in new[] { LuaCrafting.LuaRecipe.AlchemistKit, LuaCrafting.LuaRecipe.AlchemistPouch, LuaCrafting.LuaRecipe.AlchemistHat, LuaCrafting.LuaRecipe.Bomb, LuaCrafting.LuaRecipe.Helmet })
                {
                    try
                    {
                        foreach (var ri in LuaCrafting.ToRecipeInfo(r))
                            sb.AppendFormat("  {0}: id={1} creates={2} qty={3} work={4} heat={5} icon={6} drawing={7} ingr=[{8}]\n", r, ri.id, ri.Creates,
                                ri.QtyCreated, ri.work, ri.heat, ri.icon, ri.drawing,
                                IngrText(ri));
                    }
                    catch (Exception e) { sb.AppendLine("  " + r + ": " + e.Message); }
                }
                var nextId = AccessTools.Field(typeof(CookBook), "nextId");
                if (nextId != null) sb.AppendLine("CookBook.nextId = " + nextId.GetValue(null));

                // Оружейная стойка: принимаемые категории
                var rackField = AccessTools.GetDeclaredFields(typeof(ToolRack)).Where(fi => fi.IsStatic).ToList();
                foreach (var fi in rackField)
                {
                    var v = fi.GetValue(null);
                    sb.AppendLine("ToolRack." + fi.Name + " (" + fi.FieldType.Name + ") = " + Describe(v));
                }
            }
            catch (Exception e) { sb.AppendLine("ОШИБКА: " + e); }

            var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "dodo_medic_probe.txt");
            try { File.WriteAllText(path, sb.ToString()); Plugin.Log.LogInfo("Разведка медиков записана: " + path); }
            catch (Exception e) { Plugin.Log.LogWarning("Разведка медиков: " + e.Message); }
        }

        private static string IngrText(RecipeInfo ri)
        {
            var d = new Description();
            ri.AddTo(d);
            return Desc(d);
        }

        private static string Describe(object v)
        {
            if (v == null) return "null";
            var en = v as System.Collections.IEnumerable;
            if (en != null && !(v is string))
            {
                var parts = new List<string>();
                foreach (var x in en) { parts.Add(x == null ? "null" : (x is Adjectif ? x.GetType().Name : x.ToString())); if (parts.Count > 40) break; }
                return "[" + string.Join(", ", parts.ToArray()) + "]";
            }
            return v.ToString();
        }

        private static void Dump(StringBuilder sb, Factory.AssetKey key)
        {
            sb.AppendLine("\n=== " + key + " ===");
            if (!Factory.Exist(key)) { sb.AppendLine("  нет такого"); return; }
            var go = Factory.Peek(key);
            DumpGo(sb, go, "  ", 0);
        }

        private static void DumpGo(StringBuilder sb, GameObject go, string ind, int depth)
        {
            sb.AppendLine(ind + "[" + go.name + "] layer=" + go.layer + " active=" + go.activeSelf + " scale=" + go.transform.localScale + " pos=" + go.transform.localPosition + " rot=" + go.transform.localEulerAngles);
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) { sb.AppendLine(ind + "  (missing script)"); continue; }
                var t = c.GetType();
                var line = new StringBuilder(ind + "  · " + t.FullName);
                try
                {
                    var mf = c as MeshFilter;
                    if (mf != null && mf.sharedMesh != null) line.Append(" mesh=" + mf.sharedMesh.name + " v=" + mf.sharedMesh.vertexCount + " bounds=" + mf.sharedMesh.bounds);
                    var r = c as Renderer;
                    if (r != null)
                        foreach (var m in r.sharedMaterials)
                            if (m != null) line.Append(" mat=" + m.name + " shader=" + (m.shader != null ? m.shader.name : "?") + TexProps(m));
                    var col = c as Collider;
                    if (col != null) line.Append(" bounds=" + col.bounds.size + " trigger=" + col.isTrigger);
                    var ap = c as Apparence;
                    if (ap != null) line.Append(" desc=" + Desc(ap.description));
                    var rec = c as Recepteur;
                    if (rec != null)
                    {
                        var bc = AccessTools.Field(typeof(Recepteur), "_baseCapacity").GetValue(rec) as Description;
                        line.Append(" baseCapacity=" + Desc(bc) + " fonction=" + AccessTools.Field(typeof(Recepteur), "_fonction").GetValue(rec));
                    }
                    var bp = c as Blueprint;
                    if (bp != null) line.Append(" result=" + bp.BlueprintResult);
                    var ld = c as LuaDriver;
                    if (ld != null) line.Append(" handle=" + ld.Handle);
                    var ai = c as AdjectiveInfo;
                    if (ai != null) line.Append(" type=" + ai.type + " code=" + ai.codeName + " name=" + ai.descriptiveName + " UI=" + ai.UIRelevant + " transferable=" + ai.transferable);
                    if (t.Name == "Tool" || t.Name == "CraftingStation" || t.Name == "ToolRack" || t.Name == "NetworkIdentity" || t.Name == "AssetKeyHolder")
                        line.Append(" " + Fields(c));
                }
                catch (Exception e) { line.Append(" (ошибка: " + e.Message + ")"); }
                sb.AppendLine(line.ToString());
            }
            if (depth < 4)
                foreach (Transform ch in go.transform) DumpGo(sb, ch.gameObject, ind + "    ", depth + 1);
        }

        private static string TexProps(Material m)
        {
            var s = new StringBuilder();
            foreach (var p in new[] { "_MainTex", "_Diffuse", "_Normals", "_Emission", "_Specular", "_Color", "_Albedo" })
                if (m.HasProperty(p))
                {
                    if (p == "_Color") s.Append(" " + p + "=" + m.GetColor(p));
                    else { var tx = m.GetTexture(p); s.Append(" " + p + "=" + (tx != null ? tx.name : "null")); }
                }
            return s.ToString();
        }

        private static string Fields(Component c)
        {
            var parts = new List<string>();
            foreach (var f in c.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                try
                {
                    var v = f.GetValue(c);
                    if (v is Delegate) continue;
                    parts.Add(f.Name + "=" + Describe(v));
                }
                catch { }
                if (parts.Count > 30) break;
            }
            return "{" + string.Join("; ", parts.ToArray()) + "}";
        }

        private static string Desc(Description d)
        {
            if (d == null) return "null";
            return "[" + string.Join(", ", d.DicoAdjectif.Values.Select(a =>
                a.GetType().Name + (a.quantifiable != null ? "=" + a.quantifiable.valeur : "")).ToArray()) + "]";
        }
    }
}
