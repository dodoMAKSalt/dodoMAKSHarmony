using System;
using System.IO;
using System.Text;

namespace CastleStoryModSettings
{
    /// <summary>
    /// Русский лаунчер игры (Electron: resources\app\app\index.html) с панелью «Моды».
    /// Ставим свой скрипт js\dodo.js и одну строку подключения в index.html; оригинал index.html хранится рядом
    /// как index.html.dodo-orig, «Убрать» возвращает его. Это правка файлов самого лаунчера, не игры/Unity; делается
    /// только по галочке в меню настроек. Лаунчер уже правит Castle Story Plus (js\index.js) — наши правки с ним не пересекаются.
    /// </summary>
    internal static class LauncherPatch
    {
        private const string Marker = "dodoMAKSharmony launcher";
        private const string Line = "<script>/* " + Marker + " */ try { require('./js/dodo.js'); } catch (e) { console.log(e); }</script>\n";

        private static string AppDir(string gameDir) { return Path.Combine(gameDir, "resources", "app", "app"); }
        public static bool Exists(string gameDir) { return Directory.Exists(AppDir(gameDir)) && File.Exists(Path.Combine(AppDir(gameDir), "index.html")); }

        public static string SettingFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dodoMAKSharmony", "launcher.txt"); }
        }

        public static bool WantedByUser()
        {
            try { return !File.Exists(SettingFile) || File.ReadAllText(SettingFile).Trim() != "0"; } catch { return true; }
        }

        public static void SaveWanted(bool on)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(SettingFile)); File.WriteAllText(SettingFile, on ? "1" : "0"); } catch { }
        }

        private static byte[] Resource(string name)
        {
            using (var s = typeof(LauncherPatch).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var m = new MemoryStream();
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        /// <summary>Поставить/обновить. Возвращает короткое описание результата.</summary>
        public static string Install(string gameDir)
        {
            if (!Exists(gameDir)) return "лаунчер не найден";
            string app = AppDir(gameDir);
            string html = Path.Combine(app, "index.html");
            string orig = html + ".dodo-orig";
            string text = File.ReadAllText(html, Encoding.UTF8);
            if (!text.Contains(Marker) && !File.Exists(orig)) File.Copy(html, orig);

            var js = Resource("launcher/dodo.js");
            var ru = Resource("csp_ru.txt");
            if (js == null) return "скрипт лаунчера не вшит";
            File.WriteAllBytes(Path.Combine(app, "js", "dodo.js"), js);
            if (ru != null) File.WriteAllBytes(Path.Combine(app, "js", "dodo_ru.txt"), ru);

            if (!text.Contains(Marker))
            {
                int at = text.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
                if (at < 0) return "index.html лаунчера не такой, как ожидалось — не тронут";
                text = text.Substring(0, at) + Line + text.Substring(at);
                File.WriteAllText(html, text, new UTF8Encoding(false));
            }
            return "установлен";
        }

        public static string Remove(string gameDir)
        {
            if (!Exists(gameDir)) return "лаунчер не найден";
            string app = AppDir(gameDir);
            string html = Path.Combine(app, "index.html");
            string orig = html + ".dodo-orig";
            if (File.Exists(orig)) { File.Copy(orig, html, true); File.Delete(orig); }
            else
            {
                string text = File.ReadAllText(html, Encoding.UTF8);
                int i = text.IndexOf(Line, StringComparison.Ordinal);
                if (i >= 0) File.WriteAllText(html, text.Remove(i, Line.Length), new UTF8Encoding(false));
            }
            foreach (var f in new[] { "dodo.js", "dodo_ru.txt" })
            {
                var p = Path.Combine(app, "js", f);
                if (File.Exists(p)) File.Delete(p);
            }
            return "убран";
        }
    }
}
