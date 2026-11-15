using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace CastleStoryModInstaller
{
    internal sealed class InstallStatus
    {
        public bool BepInEx;
        public Version InstalledMod;
        public Version BundledMod;
    }

    /// <summary>Установка/удаление BepInEx и мода. Файлы самой игры не трогаем.</summary>
    internal static class ModInstaller
    {
        private const string ModFolder = "CastleStoryOptimizer";
        private const string ModDll = "CastleStoryOptimizer.dll";
        private const string ConfigFile = "castlestory.optimizer.cfg";

        // Что кладёт BepInEx в корень игры (для полного удаления)
        private static readonly string[] BepInExRootFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };

        private static Assembly Self => typeof(ModInstaller).Assembly;

        private static string PluginsDir(string game) => Path.Combine(game, @"BepInEx\plugins\" + ModFolder);

        public static InstallStatus GetStatus(string game)
        {
            var s = new InstallStatus { BundledMod = BundledVersion() };
            s.BepInEx = File.Exists(Path.Combine(game, "winhttp.dll")) && File.Exists(Path.Combine(game, @"BepInEx\core\BepInEx.dll"));
            var dll = Path.Combine(PluginsDir(game), ModDll);
            if (File.Exists(dll))
            {
                try { s.InstalledMod = AssemblyName.GetAssemblyName(dll).Version; }
                catch { s.InstalledMod = new Version(0, 0); }
            }
            return s;
        }

        public static bool IsGameRunning() =>
            Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameLocator.ExeName)).Length > 0;

        public static void Install(string game, bool showOverlay, Action<string> log)
        {
            // 1. BepInEx
            log("Распаковываю BepInEx 5.4.23…");
            using (var s = Self.GetManifestResourceStream("BepInEx.zip"))
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    var target = SafeCombine(game, e.FullName);
                    if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    // Конфиг doorstop не перезаписываем, если он уже есть (вдруг пользователь его правил)
                    if (e.FullName == "doorstop_config.ini" && File.Exists(target)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, overwrite: true);
                }
            }

            // 2. Мод
            log("Копирую мод…");
            var plugins = PluginsDir(game);
            Directory.CreateDirectory(plugins);
            foreach (var name in Self.GetManifestResourceNames().Where(n => n.StartsWith("mod/")))
            {
                var target = Path.Combine(plugins, name.Substring(4));
                using (var src = Self.GetManifestResourceStream(name))
                using (var dst = File.Create(target))
                    src.CopyTo(dst);
            }

            // 3. Стартовый конфиг — только если его ещё нет (свои настройки не затираем)
            var cfgDir = Path.Combine(game, @"BepInEx\config");
            var cfg = Path.Combine(cfgDir, ConfigFile);
            if (!File.Exists(cfg))
            {
                Directory.CreateDirectory(cfgDir);
                File.WriteAllText(cfg,
                    "[Profiler]\r\n\r\n## Показывать оверлей сразу при запуске (иначе — по клавише).\r\n" +
                    "ShowOverlayOnStart = " + (showOverlay ? "true" : "false") + "\r\n\r\n" +
                    "## Подробные замеры (стоят ~0.5–1 мс на кадр). Включать только для поиска лагов.\r\n" +
                    "DetailedHooks = " + (showOverlay ? "true" : "false") + "\r\n");
            }
            log("Готово.");
        }

        public static void Uninstall(string game, bool removeBepInEx, Action<string> log)
        {
            var plugins = PluginsDir(game);
            if (Directory.Exists(plugins)) Directory.Delete(plugins, recursive: true);
            var cfg = Path.Combine(game, @"BepInEx\config\" + ConfigFile);
            if (File.Exists(cfg)) File.Delete(cfg);
            log("Мод удалён.");

            if (!removeBepInEx) return;
            // Если в plugins остались чужие моды — BepInEx не трогаем
            var allPlugins = Path.Combine(game, @"BepInEx\plugins");
            if (Directory.Exists(allPlugins) && Directory.GetFileSystemEntries(allPlugins).Length > 0)
            {
                log("В BepInEx\\plugins есть другие моды — BepInEx оставлен.");
                return;
            }
            foreach (var f in BepInExRootFiles)
            {
                var p = Path.Combine(game, f);
                if (File.Exists(p)) File.Delete(p);
            }
            var bep = Path.Combine(game, "BepInEx");
            if (Directory.Exists(bep)) Directory.Delete(bep, recursive: true);
            log("BepInEx удалён. Игра в исходном состоянии.");
        }

        public static void LaunchGame() =>
            Process.Start(new ProcessStartInfo("steam://rungameid/" + GameLocator.AppId) { UseShellExecute = true });

        private static Version BundledVersion()
        {
            try
            {
                using (var s = Self.GetManifestResourceStream("mod/" + ModDll))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return Assembly.ReflectionOnlyLoad(ms.ToArray()).GetName().Version;
                }
            }
            catch { return null; }
        }

        // Защита от путей вида ../../ внутри архива
        private static string SafeCombine(string root, string relative)
        {
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
            var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Недопустимый путь в архиве: " + relative);
            return full;
        }
    }
}
