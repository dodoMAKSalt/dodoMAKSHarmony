using System;
using System.Windows.Forms;

namespace CastleStoryModSettings
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SettingsForm(args));
        }
    }
}
