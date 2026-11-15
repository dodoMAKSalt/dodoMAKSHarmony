using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CastleStoryModInstaller
{
    /// <summary>Ищет папку Castle Story во всех библиотеках Steam.</summary>
    internal static class GameLocator
    {
        public const string AppId = "227860";
        public const string ExeName = "Castle Story.exe";

        public static string Find()
        {
            foreach (var lib in SteamLibraries())
            {
                try
                {
                    var manifest = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
                    if (!File.Exists(manifest)) continue;
                    var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
                    var dir = Path.Combine(lib, "steamapps", "common", m.Success ? m.Groups[1].Value : "Castle Story");
                    if (IsGameDir(dir)) return dir;
                }
                catch { /* битая библиотека — пропускаем */ }
            }
            return null;
        }

        public static bool IsGameDir(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, ExeName)) && Directory.Exists(Path.Combine(dir, "Castle Story_Data"));

        private static IEnumerable<string> SteamLibraries()
        {
            var result = new List<string>();
            var steam = ReadReg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                        ?? ReadReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? ReadReg(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            if (steam != null)
            {
                steam = steam.Replace('/', '\\');
                result.Add(steam);
                var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                        result.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            // Запасные стандартные пути
            result.Add(@"C:\Program Files (x86)\Steam");
            result.Add(@"C:\Program Files\Steam");
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                result.Add(Path.Combine(d.Name, "SteamLibrary"));
                result.Add(Path.Combine(d.Name, "Steam"));
            }
            return result;
        }

        private static string ReadReg(RegistryKey root, string path, string name)
        {
            try
            {
                using (var k = root.OpenSubKey(path))
                    return k?.GetValue(name) as string;
            }
            catch { return null; }
        }
    }
}
