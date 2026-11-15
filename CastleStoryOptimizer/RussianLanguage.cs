using System;
using System.IO;
using BepInEx;
using Brix.NewUI.UserSettings_;
using HarmonyLib;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Русификатор: кладёт полный русский перевод в Info/Languages и включает его при каждом запуске.
    /// Основа — перевод сообщества из Мастерской (id 1925721026) + наши ~190 строк (обучение, справка).
    /// Сборка файлов: tools/build_russian.py.
    /// </summary>
    internal static class RussianLanguage
    {
        public const string LanguageName = "Русский (dodoMAKSharmony)";
        private const string FolderName = "dodoMAKSharmony_RU";

        private static bool _force;

        public static void Apply(Harmony h, bool force)
        {
            _force = force;
            try
            {
                Install();
                if (force)
                    h.Patch(AccessTools.Method(typeof(UserSettingManager), nameof(UserSettingManager.SetSettingIntoGame)),
                        prefix: new HarmonyMethod(typeof(RussianLanguage), nameof(SetSettingsPrefix)));
                Plugin.Log.LogInfo("Русификатор установлен" + (force ? " и включён по умолчанию." : "."));
            }
            catch (Exception e) { Plugin.Log.LogError("Русификатор не установлен: " + e); }
        }

        private static void Install()
        {
            var dir = Path.Combine(Path.Combine(Path.Combine(Paths.GameRootPath, "Info"), "Languages"), FolderName);
            Directory.CreateDirectory(dir);
            Write(dir, "language.csv");
            Write(dir, "meta.json");
        }

        private static void Write(string dir, string file)
        {
            byte[] data;
            using (var s = typeof(RussianLanguage).Assembly.GetManifestResourceStream("lang/" + file))
            {
                if (s == null) throw new FileNotFoundException("В DLL нет ресурса lang/" + file);
                data = new byte[s.Length];
                int read = 0;
                while (read < data.Length) read += s.Read(data, read, data.Length - read);
            }
            var path = Path.Combine(dir, file);
            if (File.Exists(path) && SameBytes(File.ReadAllBytes(path), data)) return; // уже свежий
            File.WriteAllBytes(path, data);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Перед применением настроек подменяем язык — игра применит и сохранит его сама.
        private static void SetSettingsPrefix(UserSettings settings)
        {
            if (_force && settings != null && settings.localization != null)
                settings.localization.language = LanguageName;
        }
    }
}
