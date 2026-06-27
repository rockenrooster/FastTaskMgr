using FastTaskMgr.App;
using FastTaskMgr.UI.Controls;
using System.Security.Principal;

namespace FastTaskMgr.UI.Pages;

internal sealed class SettingsPage : PageBase
{
    private readonly ComboBox _defaultPage = new();
    private readonly ComboBox _updateSpeed = new();
    private readonly ComboBox _theme = new();
    private readonly CheckBox _alwaysOnTop = new() { Text = "Always on top", AutoSize = true };
    private readonly CheckBox _minimizeOnUse = new() { Text = "Minimize on use", AutoSize = true };
    private readonly CheckBox _hideWhenMinimized = new() { Text = "Hide when minimized", AutoSize = true };
    private readonly CheckBox _alwaysStartAsAdmin = new() { Text = "Always start as admin", AutoSize = true };
    private readonly CheckBox _confirmEnd = new() { Text = "Confirm before ending process", AutoSize = true };
    private readonly CheckBox _confirmEfficiency = new() { Text = "Confirm before efficiency mode", AutoSize = true };
    private readonly Label _currentVersion = new();
    private readonly Label _latestVersion = new();
    private readonly Label _updateStatus = new();
    private readonly LoadingSpinner _updateSpinner = new();
    private readonly Button _checkUpdates = new() { Text = "Check for updates", AutoSize = true, Height = 32 };
    private readonly Button _downloadUpdate = new() { Text = "Download and Install Update", AutoSize = true, Height = 32, Enabled = false };

    public SettingsPage(AppState state)
        : base(state)
    {
        TableLayoutPanel form = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(14),
            ColumnCount = 2
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 500));
        Controls.Add(form);

        ConfigureCombo(_defaultPage, ["Processes", "Performance", "Startup apps", "Users", "Details", "Services", "Settings"]);
        ConfigureCombo(_updateSpeed, Enum.GetNames<UpdateSpeed>());
        ConfigureCombo(_theme, ["System", "Light", "Dark"]);

        AddRow(form, "Default start page", _defaultPage);
        AddRow(form, "Real-time update speed", _updateSpeed);
        AddRow(form, "Theme", _theme);
        AddRow(form, "", _alwaysOnTop);
        AddRow(form, "", _minimizeOnUse);
        AddRow(form, "", _hideWhenMinimized);
        AddRow(form, "", _alwaysStartAsAdmin);
        AddRow(form, "", _confirmEnd);
        AddRow(form, "", _confirmEfficiency);
        AddRow(form, "Updates", BuildUpdatePanel(), 150);

        Button save = new() { Text = "Save", AutoSize = true, Height = 32 };
        save.Click += (_, _) => Save();
        AddRow(form, "", save);

        _checkUpdates.Click += async (_, _) => await CheckForUpdatesAsync();
        _downloadUpdate.Click += async (_, _) => await DownloadUpdateAsync();
        State.Updates.StatusChanged += (_, _) => RefreshUpdateUi();
    }

    public override string Title => "Settings";

    public override void OnShow()
    {
        _defaultPage.SelectedItem = State.Settings.DefaultPage;
        _updateSpeed.SelectedItem = State.Settings.UpdateSpeed.ToString();
        _theme.SelectedItem = State.Settings.Theme;
        _alwaysOnTop.Checked = State.Settings.AlwaysOnTop;
        _minimizeOnUse.Checked = State.Settings.MinimizeOnUse;
        _hideWhenMinimized.Checked = State.Settings.HideWhenMinimized;
        _alwaysStartAsAdmin.Checked = State.Settings.AlwaysStartAsAdmin;
        _confirmEnd.Checked = State.Settings.ConfirmBeforeEndProcess;
        _confirmEfficiency.Checked = State.Settings.ConfirmBeforeEfficiencyMode;
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
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        Label label = new()
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        editor.Dock = DockStyle.Left;
        editor.Margin = new Padding(0, 5, 0, 5);

        form.Controls.Add(label, 0, row);
        form.Controls.Add(editor, 1, row);
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
        if (_alwaysStartAsAdmin.Checked && !IsAdministrator())
        {
            MessageBox.Show(this, "FastTaskMgr will request administrator rights the next time it starts.", "FastTaskMgr", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
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
            await State.Updates.DownloadLatestAsync();
            State.Updates.InstallDownloadedUpdate(Application.ExecutablePath);
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
        string? downloadedFile = State.Updates.DownloadedFile;
        _currentVersion.Text = State.Updates.CurrentVersion.ToString();
        _latestVersion.Text = checking ? "Checking..." : State.Updates.LastResult?.LatestVersionText ?? "Not checked";
        _checkUpdates.Enabled = !checking && !downloading;
        _downloadUpdate.Enabled = !checking && !downloading && State.Updates.LastResult?.CanDownload == true;
        _updateSpinner.Active = checking || downloading;

        if (downloading)
        {
            _updateStatus.Text = $"Downloading {progress:0}%";
        }
        else if (!string.IsNullOrWhiteSpace(downloadedFile))
        {
            _updateStatus.Text = $"Downloaded to {downloadedFile}";
        }
        else
        {
            _updateStatus.Text = State.Updates.LastResult?.Message ?? "Update status has not been checked.";
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
