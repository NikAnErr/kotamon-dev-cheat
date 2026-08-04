using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

[assembly: AssemblyTitle("Kotamon Dev Cheat Launcher")]
[assembly: AssemblyDescription("Launcher and installer for Kotamon Dev Cheat")]
[assembly: AssemblyProduct("Kotamon Dev Cheat")]
[assembly: AssemblyCompany("Kotamon")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("0.2.5.0")]
[assembly: AssemblyFileVersion("0.2.5.0")]

namespace KotamonDevCheat.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
        }
    }

    internal sealed class LauncherForm : Form
    {
        private const string Version = "0.2.5";
        private const string EmbeddedPluginName = "KotamonDevCheat.EmbeddedPlugin.dll";
        private const string EmbeddedBepInExName = "KotamonDevCheat.BepInExPayload.zip";
        private const string GameExecutableName = "TrashLover.exe";
        private const string InstallMarkerName = "KotamonDevCheat.install";

        private readonly TextBox _gamePath = new TextBox();
        private readonly Label _status = new Label();
        private readonly Button _installButton = new Button();
        private readonly Button _launchButton = new Button();
        private readonly Button _uninstallButton = new Button();

        public LauncherForm()
        {
            Text = "Kotamon Dev Cheat " + Version;
            ClientSize = new Size(620, 350);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(28, 31, 38);
            ForeColor = Color.WhiteSmoke;

            var title = new Label
            {
                Text = "KOTAMON DEV CHEAT",
                Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(80, 220, 235),
                AutoSize = true,
                Location = new Point(24, 20)
            };
            Controls.Add(title);

            var description = new Label
            {
                Text = "Выберите корневую папку KOTAMON. Launcher установит BepInEx IL2CPP,\n" +
                       "встроенный плагин и при необходимости запустит игру.",
                AutoSize = true,
                Location = new Point(27, 69)
            };
            Controls.Add(description);

            var pathLabel = new Label
            {
                Text = "Папка игры",
                AutoSize = true,
                Location = new Point(27, 121)
            };
            Controls.Add(pathLabel);

            _gamePath.Location = new Point(27, 142);
            _gamePath.Size = new Size(472, 24);
            _gamePath.Text = FindGameRoot(AppDomain.CurrentDomain.BaseDirectory) ?? string.Empty;
            Controls.Add(_gamePath);

            var browseButton = new Button
            {
                Text = "Обзор...",
                Location = new Point(509, 140),
                Size = new Size(85, 28)
            };
            browseButton.Click += BrowseButtonOnClick;
            Controls.Add(browseButton);

            _installButton.Text = "УСТАНОВИТЬ / ОБНОВИТЬ";
            _installButton.Location = new Point(27, 190);
            _installButton.Size = new Size(275, 38);
            _installButton.BackColor = Color.FromArgb(33, 130, 145);
            _installButton.ForeColor = Color.White;
            _installButton.FlatStyle = FlatStyle.Flat;
            _installButton.Click += InstallButtonOnClick;
            Controls.Add(_installButton);

            _launchButton.Text = "ЗАПУСТИТЬ ИГРУ";
            _launchButton.Location = new Point(319, 190);
            _launchButton.Size = new Size(275, 38);
            _launchButton.Click += LaunchButtonOnClick;
            Controls.Add(_launchButton);

            _uninstallButton.Text = "ДЕИНСТАЛЛЯЦИЯ";
            _uninstallButton.Location = new Point(27, 238);
            _uninstallButton.Size = new Size(567, 32);
            _uninstallButton.Click += UninstallButtonOnClick;
            Controls.Add(_uninstallButton);

            _status.Text = "Готово: BepInEx предварительно устанавливать не требуется.";
            _status.ForeColor = Color.Gainsboro;
            _status.Location = new Point(27, 286);
            _status.Size = new Size(567, 48);
            Controls.Add(_status);
        }

        private void BrowseButtonOnClick(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите корневую папку игры KOTAMON";
                dialog.SelectedPath = Directory.Exists(_gamePath.Text) ? _gamePath.Text : string.Empty;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _gamePath.Text = dialog.SelectedPath;
            }
        }

        private void InstallButtonOnClick(object sender, EventArgs e)
        {
            RunUiOperation(delegate
            {
                var root = ValidateGameRoot(_gamePath.Text);
                if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExecutableName)).Length > 0)
                    throw new InvalidOperationException("Сначала полностью закройте KOTAMON, затем повторите установку.");

                var marker = Path.Combine(root, InstallMarkerName);
                var notice = Path.Combine(root, "BepInEx", "THIRD_PARTY_NOTICES-Kotamon.txt");
                var runtimeOwned = File.Exists(marker)
                    ? ReadRuntimeOwnership(marker)
                    : !Directory.Exists(Path.Combine(root, "BepInEx")) || File.Exists(notice);
                var pluginDirectory = Path.Combine(root, "BepInEx", "plugins", "KotamonDevCheat");
                var target = Path.Combine(pluginDirectory, "KotamonDevCheat.dll");
                var temporary = target + ".new";

                BackupIfExists(Path.Combine(root, "winhttp.dll"));
                BackupIfExists(Path.Combine(root, "doorstop_config.ini"));
                BackupIfExists(Path.Combine(root, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"));
                BackupIfExists(Path.Combine(root, "BepInEx", "config", "BepInEx.cfg"));
                BackupIfExists(target);

                ExtractEmbeddedZip(EmbeddedBepInExName, root);
                Directory.CreateDirectory(pluginDirectory);
                ExtractEmbeddedPlugin(temporary);
                try
                {
                    File.Copy(temporary, target, true);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }

                File.WriteAllText(marker, "Version=" + Version + Environment.NewLine +
                                          "RuntimeOwned=" + runtimeOwned);

                _status.ForeColor = Color.FromArgb(120, 235, 145);
                _status.Text = "BepInEx и Kotamon Dev Cheat " + Version + " установлены. SHA-256: " + ShortHash(target) +
                               "\nПервый запуск может занять немного больше времени.";
            });
        }

        private void UninstallButtonOnClick(object sender, EventArgs e)
        {
            RunUiOperation(delegate
            {
                var root = ValidateGameRoot(_gamePath.Text);
                if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExecutableName)).Length > 0)
                    throw new InvalidOperationException("Сначала полностью закройте KOTAMON, затем повторите деинсталляцию.");

                var marker = Path.Combine(root, InstallMarkerName);
                var notice = Path.Combine(root, "BepInEx", "THIRD_PARTY_NOTICES-Kotamon.txt");
                var runtimeOwned = File.Exists(marker) ? ReadRuntimeOwnership(marker) : File.Exists(notice);
                var pluginDirectory = Path.Combine(root, "BepInEx", "plugins", "KotamonDevCheat");
                if (!Directory.Exists(pluginDirectory) && !File.Exists(marker) && !File.Exists(notice))
                    throw new InvalidOperationException("Установка Kotamon Dev Cheat не найдена.");

                var scope = runtimeOwned
                    ? "Будут удалены Kotamon Dev Cheat и установленная launcher’ом среда BepInEx."
                    : "Будет удалён Kotamon Dev Cheat; существующая ранее среда BepInEx будет сохранена.";
                if (MessageBox.Show(this, scope + "\n\nПродолжить?", "Деинсталляция",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                PerformUninstall(root, runtimeOwned);
                _status.ForeColor = Color.FromArgb(120, 235, 145);
                _status.Text = runtimeOwned
                    ? "Kotamon Dev Cheat и установленный launcher’ом BepInEx удалены."
                    : "Kotamon Dev Cheat удалён; исходный BepInEx сохранён.";
            });
        }

        private void LaunchButtonOnClick(object sender, EventArgs e)
        {
            RunUiOperation(delegate
            {
                var root = ValidateGameRoot(_gamePath.Text);
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(root, GameExecutableName),
                    WorkingDirectory = root,
                    UseShellExecute = true
                });
                _status.ForeColor = Color.FromArgb(120, 235, 145);
                _status.Text = "KOTAMON запущен. Меню чита по умолчанию открывается клавишей Insert.";
            });
        }

        private void RunUiOperation(Action operation)
        {
            try
            {
                _installButton.Enabled = false;
                _launchButton.Enabled = false;
                _uninstallButton.Enabled = false;
                operation();
            }
            catch (Exception exception)
            {
                _status.ForeColor = Color.FromArgb(255, 125, 125);
                _status.Text = exception.Message;
            }
            finally
            {
                _installButton.Enabled = true;
                _launchButton.Enabled = true;
                _uninstallButton.Enabled = true;
            }
        }

        private static string ValidateGameRoot(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Укажите папку игры.");

            var root = Path.GetFullPath(value.Trim().Trim('"'));
            if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, GameExecutableName)) ||
                !File.Exists(Path.Combine(root, "GameAssembly.dll")))
                throw new InvalidOperationException("Выбранная папка не является корневой папкой KOTAMON.");
            return root;
        }

        private static string FindGameRoot(string startDirectory)
        {
            var current = new DirectoryInfo(startDirectory);
            for (var depth = 0; current != null && depth < 6; depth++, current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, GameExecutableName)) &&
                    File.Exists(Path.Combine(current.FullName, "GameAssembly.dll")))
                    return current.FullName;
            }
            return null;
        }

        private static void ExtractEmbeddedPlugin(string destination)
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPluginName))
            {
                if (resource == null)
                    throw new InvalidOperationException("Встроенная DLL плагина повреждена или отсутствует.");
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    resource.CopyTo(output);
            }
        }

        private static void ExtractEmbeddedZip(string resourceName, string destinationRoot)
        {
            var normalizedRoot = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) +
                                 Path.DirectorySeparatorChar;
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (resource == null)
                    throw new InvalidOperationException("Встроенная среда BepInEx повреждена или отсутствует.");

                using (var archive = new ZipArchive(resource, ZipArchiveMode.Read, false))
                {
                    foreach (var entry in archive.Entries)
                    {
                        var target = Path.GetFullPath(Path.Combine(destinationRoot,
                            entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Архив BepInEx содержит небезопасный путь.");

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }

                        var parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent))
                            Directory.CreateDirectory(parent);
                        using (var input = entry.Open())
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                            input.CopyTo(output);
                    }
                }
            }
        }

        private static void BackupIfExists(string path)
        {
            if (!File.Exists(path))
                return;
            File.Copy(path, path + ".kotamon-backup", true);
        }

        private static bool ReadRuntimeOwnership(string marker)
        {
            if (!File.Exists(marker))
                return false;
            return File.ReadAllText(marker).IndexOf("RuntimeOwned=True", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void PerformUninstall(string root, bool runtimeOwned)
        {
            var marker = Path.Combine(root, InstallMarkerName);
            var pluginDirectory = Path.Combine(root, "BepInEx", "plugins", "KotamonDevCheat");
            if (runtimeOwned)
            {
                DeleteDirectoryIfExists(Path.Combine(root, "BepInEx"));
                DeleteDirectoryIfExists(Path.Combine(root, "dotnet"));
                RestoreOrDelete(Path.Combine(root, "winhttp.dll"), true);
                RestoreOrDelete(Path.Combine(root, "doorstop_config.ini"), true);
                DeleteFileIfExists(Path.Combine(root, ".doorstop_version"));
                DeleteFileIfExists(Path.Combine(root, "changelog.txt"));
            }
            else
            {
                RestorePluginOrDelete(pluginDirectory);
                RestoreOrDelete(Path.Combine(root, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"), false);
                RestoreOrDelete(Path.Combine(root, "BepInEx", "config", "BepInEx.cfg"), false);
                RestoreOrDelete(Path.Combine(root, "winhttp.dll"), false);
                RestoreOrDelete(Path.Combine(root, "doorstop_config.ini"), false);
            }
            DeleteFileIfExists(marker);
        }

        private static void RestorePluginOrDelete(string pluginDirectory)
        {
            var target = Path.Combine(pluginDirectory, "KotamonDevCheat.dll");
            var backup = target + ".kotamon-backup";
            if (File.Exists(backup))
            {
                File.Copy(backup, target, true);
                File.Delete(backup);
                return;
            }
            DeleteDirectoryIfExists(pluginDirectory);
        }

        private static void RestoreOrDelete(string path, bool deleteWhenNoBackup)
        {
            var backup = path + ".kotamon-backup";
            if (File.Exists(backup))
            {
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                File.Copy(backup, path, true);
                File.Delete(backup);
            }
            else if (deleteWhenNoBackup)
            {
                DeleteFileIfExists(path);
            }
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        private static string ShortHash(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = algorithm.ComputeHash(stream);
                return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
            }
        }
    }
}
