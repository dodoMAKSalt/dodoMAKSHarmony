using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Русский перевод Castle Story Plus. Их мод пишет английский текст прямо в код (часто склейкой
    /// "Only " + n + " of " + m), поэтому переводим в момент вывода — перехват UnityEngine.UI.Text.text.
    /// Словарь (Lang/csp_ru.txt, собирается tools/csp_build.py): точные строки, шаблоны с {0}…{N}
    /// и куски склеек (для строк, собранных из нескольких частей). Их файлы не трогаем.
    /// </summary>
    internal static class CspTranslator
    {
        private sealed class Template
        {
            public Regex Re;
            public string Ru;
            public string Anchor; // самый длинный кусок без плейсхолдеров — быстрая проверка Contains
        }

        private static readonly Dictionary<string, string> Exact = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<Template> Templates = new List<Template>();
        private static readonly List<KeyValuePair<string, string>> Fragments = new List<KeyValuePair<string, string>>();
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<int, bool> InputTexts = new Dictionary<int, bool>();
        private static readonly Regex Placeholder = new Regex(@"\{(\d+)\}");
        private const int CacheLimit = 20000;

        public static int Count { get { return Exact.Count + Templates.Count; } }

        public static void Apply(Harmony h)
        {
            Load();
            var text = AccessTools.TypeByName("UnityEngine.UI.Text");
            var setter = text != null ? AccessTools.PropertySetter(text, "text") : null;
            if (setter == null) { Plugin.Log.LogWarning("Перевод Castle Story Plus: не найден UnityEngine.UI.Text.text"); return; }
            h.Patch(setter, prefix: new HarmonyMethod(typeof(CspTranslator), nameof(TextPrefix)) { priority = Priority.First });
            Plugin.Log.LogInfo("Перевод Castle Story Plus: " + Exact.Count + " строк, " + Templates.Count + " шаблонов, "
                + Fragments.Count + " кусков.");
        }

        private static void Load()
        {
            using (var s = typeof(CspTranslator).Assembly.GetManifestResourceStream("lang/csp_ru.txt"))
            using (var r = new StreamReader(s, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string en = Unescape(line.Substring(0, tab)), ru = Unescape(line.Substring(tab + 1));
                    if (Placeholder.IsMatch(en)) AddTemplate(en, ru);
                    else
                    {
                        Exact[en] = ru;
                        string t = en.Trim();
                        if (t != en && !Exact.ContainsKey(t)) Exact[t] = ru.Trim();
                        // кусок склейки: начинается/кончается пробелом или знаком, достаточно длинный
                        if (t.Length >= 4 && (en != t || char.IsPunctuation(en[0]) || char.IsPunctuation(en[en.Length - 1])) && HasLetters(t, 3))
                            Fragments.Add(new KeyValuePair<string, string>(en, ru));
                    }
                }
            }
            Fragments.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            Templates.Sort((a, b) => b.Anchor.Length.CompareTo(a.Anchor.Length));
        }

        private static void AddTemplate(string en, string ru)
        {
            var parts = Placeholder.Split(en); // чётные — текст, нечётные — номера
            var sb = new StringBuilder("^");
            string anchor = "";
            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 0)
                {
                    sb.Append(Regex.Escape(parts[i]));
                    if (parts[i].Length > anchor.Length) anchor = parts[i];
                }
                else sb.Append("(?<p").Append(parts[i]).Append(">.+?)");
            }
            sb.Append('$');
            if (anchor.Trim().Length < 2) return; // "{0} {1}" — слишком общий
            try
            {
                Templates.Add(new Template { Re = new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), Ru = ru, Anchor = anchor });
            }
            catch { }
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool HasLetters(string s, int need)
        {
            int n = 0;
            foreach (char c in s) if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) { if (++n >= need) return true; }
            return false;
        }

        // ---- перехват вывода ----

        private static void TextPrefix(object __instance, ref string value)
        {
            if (value == null || value.Length < 2) return;
            string tr;
            try { tr = Translate(value); } catch { return; }
            if (ReferenceEquals(tr, value) || tr == value) return;
            // текст поля ввода (то, что печатает игрок) не трогаем
            var comp = __instance as Component;
            if (comp != null)
            {
                int id = comp.GetInstanceID();
                bool isInput;
                if (!InputTexts.TryGetValue(id, out isInput))
                {
                    isInput = false;
                    try
                    {
                        var input = AccessTools.TypeByName("UnityEngine.UI.InputField");
                        if (input != null) isInput = comp.GetComponentInParent(input) != null;
                    }
                    catch { }
                    InputTexts[id] = isInput;
                }
                if (isInput) return;
            }
            value = tr;
        }

        public static string Translate(string s)
        {
            if (!HasLetters(s, 2)) return s;
            string r;
            if (Cache.TryGetValue(s, out r)) return r;
            r = TranslateCore(s, 0) ?? s;
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[s] = r;
            return r;
        }

        private static string TranslateCore(string s, int depth)
        {
            string r;
            if (Exact.TryGetValue(s, out r)) return r;
            string t = s.Trim();
            if (t.Length != s.Length && t.Length > 0 && Exact.TryGetValue(t, out r))
                return s.Substring(0, s.IndexOf(t[0])) + r + s.Substring(s.IndexOf(t[0]) + t.Length);
            if (depth > 3) return null;

            foreach (var tp in Templates)
            {
                if (s.IndexOf(tp.Anchor, StringComparison.Ordinal) < 0) continue;
                var m = tp.Re.Match(s);
                if (!m.Success) continue;
                return Placeholder.Replace(tp.Ru, pm =>
                {
                    var g = m.Groups["p" + pm.Groups[1].Value];
                    if (!g.Success) return pm.Value;
                    return TranslateCore(g.Value, depth + 1) ?? g.Value;
                });
            }

            if (s.IndexOf('\n') >= 0)
            {
                var lines = s.Split('\n');
                bool any = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!HasLetters(lines[i], 2)) continue;
                    var l = TranslateCore(lines[i], depth + 1);
                    if (l != null && l != lines[i]) { lines[i] = l; any = true; }
                }
                if (any) return string.Join("\n", lines);
            }

            // куски склеек ("Next wave in " + time …)
            string cur = s;
            foreach (var f in Fragments)
                if (cur.IndexOf(f.Key, StringComparison.Ordinal) >= 0) cur = cur.Replace(f.Key, f.Value);
            if (cur != s) return cur;

            // короткие составные подписи ("Archer 3/5", "2 Steel") — по словам
            if (s.Length <= 48 && s.IndexOf(' ') > 0)
            {
                var words = s.Split(' ');
                bool any = false;
                for (int i = 0; i < words.Length; i++)
                {
                    string w;
                    if (words[i].Length > 1 && Exact.TryGetValue(words[i], out w)) { words[i] = w; any = true; }
                }
                if (any) return string.Join(" ", words);
            }
            return null;
        }
    }
}
