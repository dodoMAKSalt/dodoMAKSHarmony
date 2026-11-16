using System;
using System.IO;
using System.Text;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Русский лаунчер + панель «Моды»: если игру запустили не через меню настроек, мод сам ставит скрипт лаунчера
    /// (js\dodo.js + строка в index.html; оригинал — index.html.dodo-orig). Работает со следующего открытия лаунчера.
    /// Отключается галочкой в меню настроек (файл %APPDATA%\dodoMAKSharmony\launcher.txt = 0) или [Launcher] Install = false.
    /// </summary>
    internal static class LauncherInstaller
    {
        private const string Marker = "dodoMAKSharmony launcher";
        private const string Line = "<script>/* " + Marker + " */ try { require('./js/dodo.js'); } catch (e) { console.log(e); }</script>\n";

        public static void Run()
        {
            try
            {
                string setting = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dodoMAKSharmony"), "launcher.txt");
                if (File.Exists(setting) && File.ReadAllText(setting).Trim() == "0") return;
                // Мод работает внутри порта: его «корень» — папка CastleStoryPlus-Unity6, а лаунчер лежит на уровень выше
                // (в папке самой Castle Story). Ищем resources/app/app в корне и в родительской папке.
                string app = null, html = null;
                string root = BepInEx.Paths.GameRootPath;
                var candidates = new[] { root, Path.GetDirectoryName(root.TrimEnd('\\', '/')) };
                foreach (var c in candidates)
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    string a = Path.Combine(c, Path.Combine("resources", Path.Combine("app", "app")));
                    if (File.Exists(Path.Combine(a, "index.html"))) { app = a; html = Path.Combine(a, "index.html"); break; }
                }
                if (html == null) { Plugin.Log.LogInfo("Лаунчер: resources/app/app/index.html не найден рядом с игрой — перевод не ставим."); return; }

                string js = Read("launcher/dodo.js"), ru = Read("lang/csp_ru.txt");
                if (js == null) return;
                string jsPath = Path.Combine(app, Path.Combine("js", "dodo.js"));
                if (!File.Exists(jsPath) || File.ReadAllText(jsPath, Encoding.UTF8) != js) File.WriteAllText(jsPath, js, new UTF8Encoding(false));
                string ruPath = Path.Combine(app, Path.Combine("js", "dodo_ru.txt"));
                if (ru != null && !File.Exists(ruPath)) File.WriteAllText(ruPath, ru, new UTF8Encoding(false));

                string text = File.ReadAllText(html, Encoding.UTF8);
                if (text.Contains(Marker)) return;
                if (!File.Exists(html + ".dodo-orig")) File.Copy(html, html + ".dodo-orig");
                int at = text.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
                if (at < 0) return;
                File.WriteAllText(html, text.Substring(0, at) + Line + text.Substring(at), new UTF8Encoding(false));
                Plugin.Log.LogInfo("Лаунчер: поставлен русский перевод и кнопка «Моды» (откроется со следующего запуска лаунчера).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Лаунчер: не поставлен: " + e.Message); }
        }

        private static string Read(string name)
        {
            using (var s = typeof(LauncherInstaller).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }
    }
}
