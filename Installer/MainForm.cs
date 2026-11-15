using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CastleStoryModInstaller
{
    internal sealed class MainForm : Form
    {
        private readonly Label _pathLabel = new Label();
        private readonly Label _statusLabel = new Label();
        private readonly CheckBox _overlay = new CheckBox();
        private readonly Button _install = new Button();
        private readonly Button _play = new Button();
        private readonly Button _uninstall = new Button();
        private readonly Button _browse = new Button();
        private readonly Button _openFolder = new Button();
        private readonly TextBox _log = new TextBox();

        private string _game;

        private static readonly Color Bg = Color.FromArgb(30, 32, 38);
        private static readonly Color Panel = Color.FromArgb(42, 45, 53);
        private static readonly Color Fg = Color.FromArgb(230, 230, 235);
        private static readonly Color Muted = Color.FromArgb(150, 155, 165);
        private static readonly Color Accent = Color.FromArgb(232, 160, 32);

        public MainForm()
        {
            Text = "dodoMAKSharmony — мод для Castle Story";
            ClientSize = new Size(560, 430);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;

            var title = new Label
            {
                Text = "dodoMAKSharmony",
                Font = new Font("Segoe UI Semibold", 18f),
                ForeColor = Accent,
                AutoSize = true,
                Location = new Point(18, 14)
            };
            var subtitle = new Label
            {
                Text = "Меньше лагов, быстрее загрузка, русский язык. Файлы игры не меняет.",
                ForeColor = Muted,
                AutoSize = true,
                Location = new Point(20, 52)
            };

            _pathLabel.SetBounds(20, 88, 420, 40);
            _pathLabel.ForeColor = Fg;
            Style(_browse, "Указать…", 450, 90, 90, 30, false);
            _browse.Click += (s, e) => Browse();

            _statusLabel.SetBounds(20, 132, 520, 40);
            _statusLabel.ForeColor = Muted;

            _overlay.Text = "Показывать таблицу профайлера в игре (F8 — скрыть/показать)";
            _overlay.ForeColor = Fg;
            _overlay.SetBounds(20, 174, 520, 24);

            Style(_install, "Установить", 20, 208, 170, 42, true);
            Style(_play, "▶  Играть", 200, 208, 160, 42, false);
            Style(_uninstall, "Удалить мод", 370, 208, 170, 42, false);
            Style(_openFolder, "Открыть папку игры", 20, 258, 170, 30, false);

            _install.Click += (s, e) => Run(() => ModInstaller.Install(_game, _overlay.Checked, Log), "установка");
            _play.Click += (s, e) => { try { ModInstaller.LaunchGame(); } catch (Exception ex) { Error(ex); } };
            _uninstall.Click += (s, e) => Uninstall();
            _openFolder.Click += (s, e) => { if (_game != null) Process.Start("explorer.exe", "\"" + _game + "\""); };

            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Panel;
            _log.ForeColor = Fg;
            _log.BorderStyle = BorderStyle.None;
            _log.Font = new Font("Consolas", 9f);
            _log.SetBounds(20, 300, 520, 112);

            Controls.AddRange(new Control[] { title, subtitle, _pathLabel, _browse, _statusLabel, _overlay,
                _install, _play, _uninstall, _openFolder, _log });

            Load += (s, e) =>
            {
                SetGame(GameLocator.Find());
                if (_game == null) Log("Castle Story не найдена автоматически — нажми «Указать…».");
            };
        }

        private static void Style(Button b, string text, int x, int y, int w, int h, bool primary)
        {
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 84, 95);
            b.BackColor = primary ? Accent : Panel;
            b.ForeColor = primary ? Color.FromArgb(25, 25, 25) : Fg;
            b.Cursor = Cursors.Hand;
            if (primary) b.Font = new Font("Segoe UI Semibold", 10.5f);
        }

        private void SetGame(string dir)
        {
            _game = GameLocator.IsGameDir(dir) ? dir : null;
            _pathLabel.Text = _game != null ? "Игра: " + _game : "Игра: не найдена";
            Refresh_();
        }

        private void Refresh_()
        {
            bool has = _game != null;
            _install.Enabled = _uninstall.Enabled = _openFolder.Enabled = has;
            if (!has) { _statusLabel.Text = ""; return; }

            var st = ModInstaller.GetStatus(_game);
            string bundled = st.BundledMod != null ? Short(st.BundledMod) : "?";
            if (st.InstalledMod == null)
            {
                _statusLabel.Text = "Мод не установлен. Будет установлена версия " + bundled + (st.BepInEx ? "" : " (+ BepInEx).");
                _install.Text = "Установить";
                _uninstall.Enabled = st.BepInEx;
            }
            else if (st.BundledMod != null && st.InstalledMod < st.BundledMod)
            {
                _statusLabel.Text = "Установлена версия " + Short(st.InstalledMod) + ", доступна " + bundled + ".";
                _install.Text = "Обновить";
            }
            else
            {
                _statusLabel.Text = "✓ Установлена версия " + Short(st.InstalledMod) + ". Можно играть.";
                _install.Text = "Переустановить";
            }
            _statusLabel.ForeColor = st.InstalledMod != null ? Color.FromArgb(120, 200, 120) : Muted;
        }

        private static string Short(Version v) => v.Major + "." + v.Minor + "." + v.Build;

        private void Browse()
        {
            using (var d = new FolderBrowserDialog { Description = "Папка Castle Story (где лежит Castle Story.exe)" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!GameLocator.IsGameDir(d.SelectedPath))
                {
                    MessageBox.Show(this, "В этой папке нет Castle Story.exe.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                SetGame(d.SelectedPath);
            }
        }

        private void Uninstall()
        {
            var r = MessageBox.Show(this,
                "Удалить мод?\n\nДа — удалить мод и BepInEx полностью (игра станет как была).\nНет — удалить только мод, BepInEx оставить.",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            bool full = r == DialogResult.Yes;
            Run(() => ModInstaller.Uninstall(_game, full, Log), "удаление");
        }

        private void Run(Action action, string what)
        {
            if (_game == null) return;
            if (ModInstaller.IsGameRunning())
            {
                MessageBox.Show(this, "Сначала закрой Castle Story.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            UseWaitCursor = true;
            try
            {
                action();
            }
            catch (UnauthorizedAccessException)
            {
                if (MessageBox.Show(this, "Нет прав на запись в папку игры.\nПерезапустить установщик от имени администратора?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    RestartElevated();
            }
            catch (Exception ex)
            {
                Log("Ошибка (" + what + "): " + ex.Message);
                Error(ex);
            }
            finally
            {
                UseWaitCursor = false;
                Refresh_();
            }
        }

        private void RestartElevated()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true });
                Close();
            }
            catch { /* пользователь отказался в UAC */ }
        }

        private void Log(string msg)
        {
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
        }

        private void Error(Exception ex) =>
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
