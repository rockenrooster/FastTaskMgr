using FastTaskMgr.App;
using FastTaskMgr.UI.Controls;
using Microsoft.Win32;
using System.Security.Principal;

namespace FastTaskMgr.UI.Pages;

internal sealed class SettingsPage : PageBase
{
    private const string TaskManagerIfeoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe";
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FastTaskMgr.exe";
    private readonly ComboBox _defaultPage = new();
    private readonly ComboBox _updateSpeed = new();
    private readonly ComboBox _theme = new();
    private readonly CheckBox _alwaysOnTop = new() { Text = "Always on top", AutoSize = true };
    private readonly CheckBox _minimizeOnUse = new() { Text = "Minimize on use", AutoSize = true };
    private readonly CheckBox _hideWhenMinimized = new() { Text = "Hide when minimized", AutoSize = true };
    private readonly CheckBox _alwaysStartAsAdmin = new() { Text = "Always start as admin", AutoSize = true };
    private readonly CheckBox _replaceTaskManager = new() { Text = "Use FastTaskMgr for Task Manager shortcuts", AutoSize = true };
    private readonly CheckBox _confirmEnd = new() { Text = "Confirm before ending process", AutoSize = true };
    private readonly CheckBox _confirmEfficiency = new() { Text = "Confirm before efficiency mode", AutoSize = true };
    private readonly Label _installState = new();
    private readonly Label _installPath = new();
    private readonly Label _installStatus = new();
    private readonly LoadingSpinner _installSpinner = new();
    private readonly Button _installApp = new() { Text = "Install FastTaskMgr", AutoSize = true, Height = 32, Enabled = false };
    private readonly Label _currentVersion = new();
    private readonly Label _latestVersion = new();
    private readonly Label _updateStatus = new();
    private readonly LoadingSpinner _updateSpinner = new();
    private readonly Button _checkUpdates = new() { Text = "Check for updates", AutoSize = true, Height = 32 };
    private readonly Button _downloadUpdate = new() { Text = "Download and Install Update", AutoSize = true, Height = 32, Enabled = false };
    private readonly Button _save = new() { Text = "Save", AutoSize = true, Height = 32 };
    private readonly Label _saveStatus = new() { AutoSize = true, Margin = new Padding(10, 8, 0, 0) };
    private readonly System.Windows.Forms.Timer _saveStatusTimer = new() { Interval = 2400 };

    public SettingsPage(AppState state)
        : base(state)
    {
        AutoScroll = true;
        FlowLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(14),
            WrapContents = false
        };
        root.Resize += (_, _) => ResizeSections(root);
        Controls.Add(root);

        ConfigureCombo(_defaultPage, ["Processes", "Performance", "Startup apps", "Users", "Details", "Services", "Settings"]);
        ConfigureCombo(_updateSpeed, Enum.GetNames<UpdateSpeed>());
        ConfigureCombo(_theme, ["System", "Light", "Dark"]);

        TableLayoutPanel general = AddSection(root, "General");
        AddRow(general, "Default start page", _defaultPage);
        AddRow(general, "Real-time update speed", _updateSpeed);
        AddRow(general, "Theme", _theme);

        TableLayoutPanel behavior = AddSection(root, "Behavior");
        AddCheckRow(behavior, _alwaysOnTop);
        AddCheckRow(behavior, _minimizeOnUse);
        AddCheckRow(behavior, _hideWhenMinimized);
        AddCheckRow(behavior, _confirmEnd);
        AddCheckRow(behavior, _confirmEfficiency);

        TableLayoutPanel integration = AddSection(root, "Windows Integration");
        AddCheckRow(integration, _alwaysStartAsAdmin);
        AddCheckRow(integration, _replaceTaskManager);

        TableLayoutPanel installation = AddSection(root, "Installation");
        AddWideRow(installation, BuildInstallPanel());

        TableLayoutPanel updates = AddSection(root, "Updates");
        AddWideRow(updates, BuildUpdatePanel());

        TableLayoutPanel actions = AddSection(root, "Save");
        FlowLayoutPanel saveRow = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        saveRow.Controls.Add(_save);
        saveRow.Controls.Add(_saveStatus);
        AddWideRow(actions, saveRow);

        _save.Click += (_, _) => Save();
        _saveStatusTimer.Tick += (_, _) => ClearSaveStatus();
        _installApp.Click += async (_, _) => await InstallAppAsync();
        _checkUpdates.Click += async (_, _) => await CheckForUpdatesAsync();
        _downloadUpdate.Click += async (_, _) => await DownloadUpdateAsync();
        State.Updates.StatusChanged += (_, _) => RefreshUpdateUi();
    }

    public override string Title => "Settings";
    public override bool UsesSearch => false;

    public override void OnShow()
    {
        _defaultPage.SelectedItem = State.Settings.DefaultPage;
        _updateSpeed.SelectedItem = State.Settings.UpdateSpeed.ToString();
        _theme.SelectedItem = State.Settings.Theme;
        _alwaysOnTop.Checked = State.Settings.AlwaysOnTop;
        _minimizeOnUse.Checked = State.Settings.MinimizeOnUse;
        _hideWhenMinimized.Checked = State.Settings.HideWhenMinimized;
        _alwaysStartAsAdmin.Checked = State.Settings.AlwaysStartAsAdmin;
        _replaceTaskManager.Checked = IsTaskManagerReplacementEnabled();
        _confirmEnd.Checked = State.Settings.ConfirmBeforeEndProcess;
        _confirmEfficiency.Checked = State.Settings.ConfirmBeforeEfficiencyMode;
        ClearSaveStatus();
        RefreshInstallUi();
        RefreshUpdateUi();
        if (State.Updates.LastResult is null && !State.Updates.IsChecking)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    private static void ConfigureCombo(ComboBox combo, string[] values)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Width = 220;
        combo.Items.AddRange(values);
    }

    private static TableLayoutPanel AddSection(FlowLayoutPanel root, string title)
    {
        GroupBox group = new()
        {
            Text = title,
            Width = 720,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(10)
        };

        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(8, 12, 8, 8)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.Controls.Add(panel);
        root.Controls.Add(group);
        return panel;
    }

    private static void ResizeSections(FlowLayoutPanel root)
    {
        int width = Math.Max(520, root.ClientSize.Width - root.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 6);
        foreach (Control control in root.Controls)
        {
            control.Width = width;
        }
    }

    private Control BuildInstallPanel()
    {
        TableLayoutPanel panel = new()
        {
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 2, 0, 2)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));

        ConfigureValueLabel(_installState);
        ConfigureValueLabel(_installPath);
        ConfigureValueLabel(_installStatus);
        _installPath.MaximumSize = new Size(420, 0);
        _installStatus.MaximumSize = new Size(420, 0);

        FlowLayoutPanel actions = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        _installSpinner.Margin = new Padding(8, 6, 4, 0);
        actions.Controls.Add(_installApp);
        actions.Controls.Add(_installSpinner);

        AddUpdateRow(panel, "State", _installState);
        AddUpdateRow(panel, "Path", _installPath);
        AddUpdateRow(panel, "Status", _installStatus);
        AddUpdateRow(panel, "", actions);
        return panel;
    }

    private Control BuildUpdatePanel()
    {
        TableLayoutPanel panel = new()
        {
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 2, 0, 2)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));

        ConfigureValueLabel(_currentVersion);
        ConfigureValueLabel(_latestVersion);
        ConfigureValueLabel(_updateStatus);
        _updateStatus.MaximumSize = new Size(360, 0);

        FlowLayoutPanel actions = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        _updateSpinner.Margin = new Padding(8, 6, 4, 0);
        actions.Controls.Add(_checkUpdates);
        actions.Controls.Add(_updateSpinner);
        actions.Controls.Add(_downloadUpdate);

        AddUpdateRow(panel, "Current", _currentVersion);
        AddUpdateRow(panel, "Latest", _latestVersion);
        AddUpdateRow(panel, "Status", _updateStatus);
        AddUpdateRow(panel, "", actions);
        return panel;
    }

    private static void ConfigureValueLabel(Label label)
    {
        label.AutoSize = true;
        label.Margin = new Padding(0, 3, 0, 3);
    }

    private static void AddUpdateRow(TableLayoutPanel panel, string labelText, Control value)
    {
        int row = panel.RowStyles.Count;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label label = new()
        {
            Text = labelText,
            AutoSize = true,
            Margin = new Padding(0, 3, 10, 3),
            ForeColor = Color.DimGray
        };

        panel.Controls.Add(label, 0, row);
        panel.Controls.Add(value, 1, row);
    }

    private static void AddRow(TableLayoutPanel form, string labelText, Control editor, int height = 40)
    {
        int row = form.RowStyles.Count;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label label = new()
        {
            Text = labelText,
            AutoSize = true,
            Margin = new Padding(0, 8, 12, 8),
            TextAlign = ContentAlignment.MiddleLeft
        };
        editor.Anchor = AnchorStyles.Left;
        editor.Margin = new Padding(0, 4, 0, 4);

        form.Controls.Add(label, 0, row);
        form.Controls.Add(editor, 1, row);
    }

    private static void AddCheckRow(TableLayoutPanel form, Control editor)
    {
        int row = form.RowStyles.Count;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.Anchor = AnchorStyles.Left;
        editor.Margin = new Padding(0, 7, 0, 7);
        form.Controls.Add(editor, 0, row);
        form.SetColumnSpan(editor, 2);
    }

    private static void AddWideRow(TableLayoutPanel form, Control editor)
    {
        int row = form.RowStyles.Count;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.Anchor = AnchorStyles.Left;
        editor.Margin = new Padding(0, 4, 0, 4);
        form.Controls.Add(editor, 0, row);
        form.SetColumnSpan(editor, 2);
    }

    private void Save()
    {
        State.Settings.DefaultPage = Convert.ToString(_defaultPage.SelectedItem) ?? "Processes";
        State.Settings.UpdateSpeed = Enum.TryParse(Convert.ToString(_updateSpeed.SelectedItem), out UpdateSpeed speed) ? speed : UpdateSpeed.Normal;
        State.Settings.Theme = Convert.ToString(_theme.SelectedItem) ?? "System";
        State.Settings.AlwaysOnTop = _alwaysOnTop.Checked;
        State.Settings.MinimizeOnUse = _minimizeOnUse.Checked;
        State.Settings.HideWhenMinimized = _hideWhenMinimized.Checked;
        State.Settings.AlwaysStartAsAdmin = _alwaysStartAsAdmin.Checked;
        State.Settings.ConfirmBeforeEndProcess = _confirmEnd.Checked;
        State.Settings.ConfirmBeforeEfficiencyMode = _confirmEfficiency.Checked;
        State.SaveSettings();
        bool integrationSaved = ApplyTaskManagerReplacement();
        ShowSaveStatus(integrationSaved ? "Saved" : "Saved app settings; integration unchanged", integrationSaved);
        if (_alwaysStartAsAdmin.Checked && !IsAdministrator())
        {
            MessageBox.Show(this, "FastTaskMgr will request administrator rights the next time it starts.", "FastTaskMgr", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private bool ApplyTaskManagerReplacement()
    {
        try
        {
            SetTaskManagerReplacement(_replaceTaskManager.Checked);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            _replaceTaskManager.Checked = IsTaskManagerReplacementEnabled();
            MessageBox.Show(this, "Run FastTaskMgr as administrator to change the Task Manager shortcut setting.", "FastTaskMgr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _replaceTaskManager.Checked = IsTaskManagerReplacementEnabled();
            MessageBox.Show(this, ex.Message, "FastTaskMgr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void ShowSaveStatus(string text, bool success)
    {
        _saveStatusTimer.Stop();
        _saveStatus.Text = text;
        _saveStatus.ForeColor = success ? Color.ForestGreen : Color.DarkOrange;
        _save.BackColor = success ? Color.FromArgb(218, 244, 226) : Color.FromArgb(255, 241, 204);
        _saveStatusTimer.Start();
    }

    private void ClearSaveStatus()
    {
        _saveStatusTimer.Stop();
        _saveStatus.Text = "";
        _save.BackColor = SystemColors.Control;
    }

    private async Task CheckForUpdatesAsync()
    {
        await State.Updates.CheckAsync();
        RefreshUpdateUi();
    }

    private async Task DownloadUpdateAsync()
    {
        try
        {
            string setupPath = await State.Updates.DownloadLatestAsync();
            State.Updates.InstallDownloadedUpdate(setupPath);
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RefreshUpdateUi();
        }
    }

    private async Task InstallAppAsync()
    {
        try
        {
            string setupPath = await State.Updates.DownloadInstallerAsync();
            State.Updates.InstallDownloadedUpdate(setupPath);
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RefreshUpdateUi();
        }
    }

    private void RefreshUpdateUi()
    {
        if (InvokeRequired && IsHandleCreated)
        {
            BeginInvoke(RefreshUpdateUi);
            return;
        }

        bool checking = State.Updates.IsChecking;
        bool downloading = State.Updates.IsDownloading;
        double progress = State.Updates.DownloadProgress;
        _currentVersion.Text = $"v{State.Updates.CurrentVersion}";
        _latestVersion.Text = checking ? "Checking..." : State.Updates.LastResult?.LatestVersionText ?? "Not checked";
        _checkUpdates.Enabled = !checking && !downloading;
        _downloadUpdate.Enabled = !checking && !downloading && State.Updates.LastResult?.CanDownload == true;
        _updateSpinner.Active = checking || downloading;
        RefreshInstallUi();

        if (downloading)
        {
            _updateStatus.Text = $"Downloading {progress:0}%";
        }
        else
        {
            _updateStatus.Text = State.Updates.LastResult?.Message ?? "Update status has not been checked.";
        }
    }

    private void RefreshInstallUi()
    {
        string? installedPath = ReadInstalledPath();
        bool installedHere = PathsEqual(installedPath, Application.ExecutablePath);
        bool checking = State.Updates.IsChecking;
        bool downloading = State.Updates.IsDownloading;

        _installState.Text = installedHere ? "Installed" : "Portable mode";
        _installPath.Text = installedHere ? installedPath ?? Application.ExecutablePath : Application.ExecutablePath;
        _installApp.Enabled = !installedHere && !checking && !downloading && State.Updates.LastResult?.CanInstall == true;
        _installSpinner.Active = checking || downloading;

        if (installedHere)
        {
            _installStatus.Text = "FastTaskMgr is installed.";
        }
        else if (downloading)
        {
            _installStatus.Text = $"Downloading installer {State.Updates.DownloadProgress:0}%";
        }
        else if (checking)
        {
            _installStatus.Text = "Checking installer...";
        }
        else
        {
            _installStatus.Text = State.Updates.LastResult?.CanInstall == true
                ? "Signed installer is ready."
                : "Signed installer has not been checked.";
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? ReadInstalledPath()
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(AppPathsKey);
            return Convert.ToString(key?.GetValue(""));
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }

    private static bool IsTaskManagerReplacementEnabled() => DebuggerTargetsThisApp(ReadTaskManagerDebugger());

    private static string? ReadTaskManagerDebugger()
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(TaskManagerIfeoKey);
        return Convert.ToString(key?.GetValue("Debugger"));
    }

    private static void SetTaskManagerReplacement(bool enabled)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (!enabled)
        {
            using RegistryKey? existingKey = root.OpenSubKey(TaskManagerIfeoKey, writable: true);
            string? existing = Convert.ToString(existingKey?.GetValue("Debugger"));
            if (existingKey is not null && DebuggerTargetsThisApp(existing))
            {
                existingKey.DeleteValue("Debugger", throwOnMissingValue: false);
            }

            return;
        }

        using RegistryKey key = root.CreateSubKey(TaskManagerIfeoKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the Task Manager registry key.");
        string? current = Convert.ToString(key.GetValue("Debugger"));
        if (!string.IsNullOrWhiteSpace(current) && !DebuggerTargetsThisApp(current))
        {
            throw new InvalidOperationException("Task Manager is already redirected by another debugger value.");
        }

        key.SetValue("Debugger", QuotePath(Application.ExecutablePath), RegistryValueKind.String);
    }

    private static bool DebuggerTargetsThisApp(string? debugger)
    {
        string expected = QuotePath(Application.ExecutablePath);
        string value = debugger?.Trim() ?? "";
        return value.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(expected + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuotePath(string path) => "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
