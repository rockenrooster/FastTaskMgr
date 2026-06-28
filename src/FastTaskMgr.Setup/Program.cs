using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace FastTaskMgr.Setup;

internal static class Program
{
    private const string AppName = "FastTaskMgr";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-check", StringComparer.OrdinalIgnoreCase))
        {
            using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("FastTaskMgr.exe");
            return resource is null ? 1 : 0;
        }

        if (!IsAdmin())
        {
            RelaunchElevated(args);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        if (args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Installer.Uninstall();
            return 0;
        }

        Application.Run(new SetupForm());
        return 0;
    }

    private static bool IsAdmin()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchElevated(string[] args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Application.ExecutablePath,
            Arguments = string.Join(" ", args.Select(QuoteArgument)),
            UseShellExecute = true,
            Verb = "runas"
        };
        Process.Start(startInfo);
    }

    private static string QuoteArgument(string value) =>
        value.Contains(' ') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;

    private sealed class SetupForm : Form
    {
        private readonly TextBox _installDir = new() { Width = 360 };
        private readonly CheckBox _desktop = new() { Text = "Create desktop shortcut", AutoSize = true, Checked = true };
        private readonly CheckBox _startMenu = new() { Text = "Create Start Menu shortcut", AutoSize = true, Checked = true };
        private readonly CheckBox _launch = new() { Text = "Launch FastTaskMgr after install", AutoSize = true, Checked = true };
        private readonly Label _status = new() { AutoSize = true };
        private readonly Button _install = new() { Text = "Install", Width = 96, Height = 32 };
        private readonly Button _cancel = new() { Text = "Cancel", Width = 96, Height = 32 };

        public SetupForm()
        {
            Text = "FastTaskMgr Setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 330);
            Icon? icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null)
            {
                Icon = icon;
            }

            Label title = new()
            {
                Text = "Install FastTaskMgr",
                Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(22, 20)
            };
            Label subtitle = new()
            {
                Text = "Fast. Lightweight. Native.",
                AutoSize = true,
                Location = new Point(24, 58)
            };

            Label installLabel = new() { Text = "Install location", AutoSize = true, Location = new Point(24, 102) };
            _installDir.Text = Installer.DefaultInstallDir;
            _installDir.Location = new Point(24, 126);
            Button browse = new() { Text = "Browse...", Width = 90, Height = 27, Location = new Point(398, 124) };
            browse.Click += (_, _) => BrowseInstallDir();

            _startMenu.Location = new Point(24, 168);
            _desktop.Location = new Point(24, 198);
            _launch.Location = new Point(24, 228);
            _status.Location = new Point(24, 270);
            _install.Location = new Point(338, 278);
            _cancel.Location = new Point(444, 278);
            _install.Click += (_, _) => Install();
            _cancel.Click += (_, _) => Close();

            Controls.AddRange([title, subtitle, installLabel, _installDir, browse, _startMenu, _desktop, _launch, _status, _install, _cancel]);
        }

        private void BrowseInstallDir()
        {
            using FolderBrowserDialog dialog = new()
            {
                Description = "Choose install location",
                SelectedPath = _installDir.Text
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _installDir.Text = dialog.SelectedPath;
            }
        }

        private void Install()
        {
            try
            {
                _install.Enabled = false;
                _status.ForeColor = Color.Black;
                _status.Text = "Installing...";
                Update();

                string exe = Installer.Install(_installDir.Text, _startMenu.Checked, _desktop.Checked);
                _status.ForeColor = Color.ForestGreen;
                _status.Text = "Installed.";
                if (_launch.Checked)
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                }

                Close();
            }
            catch (Exception ex)
            {
                _install.Enabled = true;
                _status.ForeColor = Color.Firebrick;
                _status.Text = ex.Message;
            }
        }
    }

    private static class Installer
    {
        public static string DefaultInstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

        public static string Install(string installDir, bool startMenu, bool desktop)
        {
            Directory.CreateDirectory(installDir);
            string targetExe = Path.Combine(installDir, "FastTaskMgr.exe");
            string setupExe = Path.Combine(installDir, "FastTaskMgr-Setup.exe");

            StopInstalledApp(targetExe);
            ExtractApp(targetExe);
            File.Copy(Application.ExecutablePath, setupExe, overwrite: true);

            if (startMenu)
            {
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), @"Programs\FastTaskMgr\FastTaskMgr.lnk"), targetExe);
            }

            if (desktop)
            {
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "FastTaskMgr.lnk"), targetExe);
            }

            RegisterAppPath(targetExe, installDir);
            RegisterUninstall(targetExe, setupExe, installDir);
            return targetExe;
        }

        public static void Uninstall()
        {
            string installDir = DefaultInstallDir;
            using RegistryKey? uninstall = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FastTaskMgr");
            string? registeredDir = Convert.ToString(uninstall?.GetValue("InstallLocation"));
            if (!string.IsNullOrWhiteSpace(registeredDir))
            {
                installDir = registeredDir;
            }

            string targetExe = Path.Combine(installDir, "FastTaskMgr.exe");
            StopInstalledApp(targetExe);
            DeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "FastTaskMgr.lnk"));
            DeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), @"Programs\FastTaskMgr"));
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FastTaskMgr.exe", throwOnMissingSubKey: false);
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FastTaskMgr", throwOnMissingSubKey: false);
            ScheduleDirectoryDelete(installDir);
        }

        private static void ExtractApp(string targetExe)
        {
            using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("FastTaskMgr.exe");
            if (resource is null)
            {
                throw new InvalidOperationException("Installer payload is missing.");
            }

            using FileStream output = File.Create(targetExe);
            resource.CopyTo(output);
        }

        private static void StopInstalledApp(string targetExe)
        {
            foreach (Process process in Process.GetProcessesByName("FastTaskMgr"))
            {
                try
                {
                    if (process.MainModule?.FileName.Equals(targetExe, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch
                {
                }
            }
        }

        private static void RegisterAppPath(string targetExe, string installDir)
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FastTaskMgr.exe");
            key.SetValue("", targetExe);
            key.SetValue("Path", installDir);
        }

        private static void RegisterUninstall(string targetExe, string setupExe, string installDir)
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FastTaskMgr");
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayIcon", targetExe);
            key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "");
            key.SetValue("Publisher", "FastTaskMgr");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{setupExe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        private static void CreateShortcut(string shortcutPath, string targetExe)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
            object shell = Activator.CreateInstance(shellType)!;
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])!;
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetExe]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(targetExe)!]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{targetExe},0"]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }

        private static void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static void ScheduleDirectoryDelete(string path)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"FastTaskMgr-uninstall-{Guid.NewGuid():N}.ps1");
            string escapedPath = EscapePowerShell(path);
            string script = string.Join(Environment.NewLine, [
                $"Wait-Process -Id {Environment.ProcessId} -Timeout 30 -ErrorAction SilentlyContinue",
                "for ($attempt = 0; $attempt -lt 40; $attempt++) {",
                "    try {",
                $"        Remove-Item -LiteralPath '{escapedPath}' -Recurse -Force -ErrorAction Stop",
                "        break",
                "    }",
                "    catch {",
                "        Start-Sleep -Milliseconds 500",
                "    }",
                "}",
                "Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue"
            ]);
            File.WriteAllText(scriptPath, script, Encoding.UTF8);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File {QuoteArgument(scriptPath)}",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    }
}
