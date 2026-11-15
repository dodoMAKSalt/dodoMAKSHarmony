using System;
using System.IO;
using System.Reflection;

namespace CastleStoryOptimizer
{
    /// <summary>
    /// Совместная работа с модом BetterThanCastle (github.com/NNLEKKK/BetterThanCastle, GPL v3; его код мы не копируем).
    /// Если он установлен, наш мод отключает то, что у них сделано не хуже (пауза сборщика мусора и уборка
    /// после загрузки, опрос «каждую секунду» в загрузчике), и обходит общие места (круговое меню, клавиши).
    /// </summary>
    internal static class BtcCompat
    {
        public const string Guid = "com.btc.betterthancastle";
        public static bool Present { get; private set; }
        private static MethodInfo _btcLivreRead;
        private static bool _livreResolved;

        public static void Detect()
        {
            try
            {
                Present = Directory.GetFiles(BepInEx.Paths.PluginPath, "BetterThanCastle.dll", SearchOption.AllDirectories).Length > 0;
            }
            catch { Present = false; }
            if (Present) Plugin.Log.LogInfo("Найден мод BetterThanCastle — включаю режим совместной работы.");
        }

        /// <summary>Их перехватчик чтения файлов кругового меню (BtcLivre.Read), если есть.</summary>
        public static MethodInfo BtcLivreRead
        {
            get
            {
                if (_livreResolved) return _btcLivreRead;
                _livreResolved = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "BetterThanCastle") continue;
                    var t = asm.GetType("BetterThanCastle.Patches.BtcLivre");
                    if (t != null) _btcLivreRead = t.GetMethod("Read", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                }
                return _btcLivreRead;
            }
        }

        /// <summary>Прочитать файл так, чтобы их добавки в круговое меню тоже попали (если их мод есть).</summary>
        public static string ReadLivre(string path)
        {
            var m = BtcLivreRead;
            if (m != null)
            {
                try { return (string)m.Invoke(null, new object[] { path }); }
                catch (Exception e) { Plugin.Log.LogWarning("BetterThanCastle: чтение меню через их мод не удалось: " + e.Message); }
            }
            return File.ReadAllText(path);
        }
    }
}
