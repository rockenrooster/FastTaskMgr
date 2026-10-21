using FastTaskMgr.UI.Pages;
using FastTaskMgr.UI.Theme;

namespace FastTaskMgr.App;

internal sealed class MainForm : Form
{
    /// <summary>Also used by a second launch to find this window, so keep it constant.</summary>
    public const string WindowTitle = "FastTaskMgr";
    private static readonly string[] PageKeys = ["Processes", "Performance", "Startup apps", "Users", "Details", "Services", "Settings"];

    private readonly AppState _state;
    private readonly Panel _pageHost = new();
    private readonly TableLayoutPanel _header = new();
    private readonly Label _title = new();
    private readonly TextBox _search = new();
    private readonly Dictionary<string, Button> _navButtons = [];
    private readonly Dictionary<string, PageBase> _pages = [];
    private readonly Font _navSelectedFont;
    private readonly NotifyIcon _trayIcon = new();
    private PageBase? _currentPage;
    private bool _updateCheckStarted;
    private bool _suspended;
    private FormWindowState _restoreState = FormWindowState.Normal;

    public MainForm(AppState state)
    {
        _state = state;
        _navSelectedFont = new Font(Font, FontStyle.Bold);
        Text = WindowTitle;
        Icon? appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
        {
            Icon = appIcon;
        }

        MinimumSize = new Size(920, 600);
        TopMost = _state.Settings.AlwaysOnTop;
        RestoreWindowBounds();

        BuildTrayIcon();
        BuildLayout();
        _state.SettingsChanged += (_, _) => ApplySettings();
        _state.DetailsNavigationRequested += (_, pid) =>
        {
            ShowPage("Details");
            if (_pages.GetValueOrDefault("Details") is DetailsPage details)
            {
                details.FocusProcess(pid);
            }
        };
        ApplySettings();
        ShowPage(string.IsNullOrWhiteSpace(_state.Settings.LastPage) ? _state.Settings.DefaultPage : _state.Settings.LastPage);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartPerformanceWarmup();
        await StartLazyUpdateCheckAsync();
    }

    // The first PDH touch costs over a second. Pay it here in the background so the
    // Performance page has warm counters and a cached sample by the time it is opened.
    private void StartPerformanceWarmup()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _ = _state.Performance.Sample();
                await Task.Delay(1000);
                _ = _state.Performance.Sample();
            }
            catch
            {
            }
        });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SingleInstance.AllowActivationFrom(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == SingleInstance.ActivateMessage && m.Msg != 0)
        {
            RestoreFromTray();
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _currentPage?.DeactivatePage();
        SaveWindowBounds();
        // Persist without raising SettingsChanged: re-theming and timer restarts are wasted work on exit.
        _ = _state.Settings.TrySave();
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != FormWindowState.Minimized)
        {
            _restoreState = WindowState;
        }
        else if (_state.Settings.HideWhenMinimized)
        {
            Hide();
        }

        UpdateSuspended();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateSuspended();
    }

    // Nothing is on screen while minimized or hidden in the tray, so live pages stop sampling.
    private void UpdateSuspended()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        bool suspended = !Visible || WindowState == FormWindowState.Minimized;
        if (suspended == _suspended)
        {
            return;
        }

        _suspended = suspended;
        _currentPage?.SetSuspended(suspended);
    }

    private void RestoreWindowBounds()
    {
        AppSettings settings = _state.Settings;
        Size = new Size(settings.WindowWidth, settings.WindowHeight);
        bool positionSaved = settings.WindowLeft != AppSettings.UnsetPosition || settings.WindowTop != AppSettings.UnsetPosition;
        Rectangle bounds = new(settings.WindowLeft, settings.WindowTop, Width, Height);
        if (positionSaved && IsTitleBarOnScreen(bounds))
        {
            StartPosition = FormStartPosition.Manual;
            Location = bounds.Location;
        }

        if (settings.WindowMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    // Bounds read while minimized or maximized are not the user's window size (a minimized
    // window sits at -32000,-32000), so persist the restore bounds plus the maximized flag.
    private void SaveWindowBounds()
    {
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        AppSettings settings = _state.Settings;
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = WindowState == FormWindowState.Maximized
            || (WindowState == FormWindowState.Minimized && _restoreState == FormWindowState.Maximized);
    }

    // A monitor can be unplugged between runs; only reuse a position whose title bar is still reachable.
    private static bool IsTitleBarOnScreen(Rectangle bounds)
    {
        Rectangle titleBar = new(bounds.Left + 40, bounds.Top, Math.Max(1, bounds.Width - 80), 30);
        return Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(titleBar));
    }

    private void BuildTrayIcon()
    {
        _trayIcon.Icon = Icon;
        _trayIcon.Text = WindowTitle;
        ContextMenuStrip menu = new();
        menu.Items.Add("Restore", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = _restoreState;
        }

        Activate();
    }

    private void BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        FlowLayoutPanel nav = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(8),
            WrapContents = false
        };
        root.Controls.Add(nav, 0, 0);

        Label brand = new()
        {
            Text = "FastTaskMgr",
            AutoSize = false,
            Height = 42,
            Width = 156,
            Font = _navSelectedFont,
            TextAlign = ContentAlignment.MiddleLeft
        };
        nav.Controls.Add(brand);

        foreach (string page in PageKeys)
        {
            Button button = new()
            {
                Text = page,
                Width = 156,
                Height = 34,
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => ShowPage(page);
            if (page == "Settings")
            {
                button.Paint += PaintSettingsUpdateMarker;
            }

            nav.Controls.Add(button);
            _navButtons[page] = button;
        }

        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(content, 1, 0);

        _header.Dock = DockStyle.Fill;
        _header.ColumnCount = 2;
        _header.Padding = new Padding(10, 8, 10, 6);
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Controls.Add(_header, 0, 0);

        _title.Dock = DockStyle.Fill;
        _title.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _header.Controls.Add(_title, 0, 0);

        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Search";
        _search.Margin = new Padding(4, 6, 0, 6);
        _search.TextChanged += (_, _) => _currentPage?.ApplySearch(_search.Text);
        _header.Controls.Add(_search, 1, 0);

        _pageHost.Dock = DockStyle.Fill;
        content.Controls.Add(_pageHost, 0, 1);
    }

    // Pages are built on first visit and themed as they are created: most sessions only ever
    // open one or two of them, and building all seven delayed the first paint.
    private PageBase? GetOrCreatePage(string key)
    {
        if (_pages.TryGetValue(key, out PageBase? page))
        {
            return page;
        }

        page = key switch
        {
            "Processes" => new ProcessesPage(_state),
            "Performance" => new PerformancePage(_state),
            "Startup apps" => new StartupPage(_state),
            "Users" => new UsersPage(_state),
            "Details" => new DetailsPage(_state),
            "Services" => new ServicesPage(_state),
            "Settings" => new SettingsPage(_state),
            _ => null
        };
        if (page is null)
        {
            return null;
        }

        page.Visible = false;
        AppTheme.Apply(page, _state.Settings.Theme);
        _pages[key] = page;
        _pageHost.Controls.Add(page);
        return page;
    }

    private void ShowPage(string key)
    {
        PageBase? page = GetOrCreatePage(key);
        if (page is null)
        {
            key = "Processes";
            page = GetOrCreatePage(key)!;
        }

        if (_currentPage is not null)
        {
            _currentPage.DeactivatePage();
            _currentPage.Visible = false;
        }

        page.Visible = true;
        page.BringToFront();
        _currentPage = page;
        _state.Settings.LastPage = key;
        _title.Text = page.Title;
        SetSearchVisible(page.UsesSearch);
        _search.Text = "";
        page.SetSuspended(_suspended);
        page.ActivatePage();

        foreach ((string navKey, Button button) in _navButtons)
        {
            button.Font = navKey == key ? _navSelectedFont : Font;
        }
    }

    private void ApplySettings()
    {
        TopMost = _state.Settings.AlwaysOnTop;
        _trayIcon.Visible = _state.Settings.HideWhenMinimized;
        AppTheme.Apply(this, _state.Settings.Theme);
        UpdateSettingsMarker();
    }

    private void SetSearchVisible(bool visible)
    {
        _search.Visible = visible;
        _header.ColumnStyles[0].SizeType = visible ? SizeType.Absolute : SizeType.Percent;
        _header.ColumnStyles[0].Width = visible ? 260 : 100;
        _header.ColumnStyles[1].SizeType = visible ? SizeType.Percent : SizeType.Absolute;
        _header.ColumnStyles[1].Width = visible ? 100 : 0;
    }

    private void UpdateSettingsMarker()
    {
        if (_navButtons.TryGetValue("Settings", out Button? button))
        {
            button.Invalidate();
        }
    }

    private void PaintSettingsUpdateMarker(object? sender, PaintEventArgs e)
    {
        if (!_state.IsUpdateAvailable || sender is not Button button)
        {
            return;
        }

        int size = 8;
        Rectangle marker = new(button.ClientSize.Width - size - 12, (button.ClientSize.Height - size) / 2, size, size);
        using SolidBrush brush = new(Color.FromArgb(0, 120, 215));
        e.Graphics.FillEllipse(brush, marker);
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (!InvokeRequired)
        {
            action();
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window handle is gone (shutting down); nothing left to update.
        }
    }

    private async Task StartLazyUpdateCheckAsync()
    {
        if (_updateCheckStarted)
        {
            return;
        }

        _updateCheckStarted = true;
        await Task.Delay(1500);
        if (IsDisposed)
        {
            return;
        }

        _state.Updates.StatusChanged += (_, _) => RunOnUiThread(UpdateSettingsMarker);
        if (_state.Updates.LastResult is null && !_state.Updates.IsChecking)
        {
            await _state.Updates.CheckAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _trayIcon.Dispose();
            _navSelectedFont.Dispose();
        }
    }
}
