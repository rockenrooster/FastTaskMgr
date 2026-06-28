using FastTaskMgr.UI.Pages;
using FastTaskMgr.UI.Theme;

namespace FastTaskMgr.App;

internal sealed class MainForm : Form
{
    private readonly AppState _state;
    private readonly Panel _pageHost = new();
    private readonly TableLayoutPanel _header = new();
    private readonly Label _title = new();
    private readonly TextBox _search = new();
    private readonly Dictionary<string, Button> _navButtons = [];
    private readonly Dictionary<string, PageBase> _pages = [];
    private readonly Font _navSelectedFont;
    private PageBase? _currentPage;
    private bool _updateCheckStarted;

    public MainForm(AppState state)
    {
        _state = state;
        _navSelectedFont = new Font(Font, FontStyle.Bold);
        Text = "FastTaskMgr";
        Icon? appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
        {
            Icon = appIcon;
        }

        MinimumSize = new Size(920, 600);
        Size = new Size(_state.Settings.WindowWidth, _state.Settings.WindowHeight);
        TopMost = _state.Settings.AlwaysOnTop;

        if (_state.Settings.WindowLeft >= 0 && _state.Settings.WindowTop >= 0)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(_state.Settings.WindowLeft, _state.Settings.WindowTop);
        }

        BuildLayout();
        RegisterPages();
        _state.SettingsChanged += (_, _) => ApplySettings();
        ShowPage(string.IsNullOrWhiteSpace(_state.Settings.LastPage) ? _state.Settings.DefaultPage : _state.Settings.LastPage);
        ApplySettings();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await StartLazyUpdateCheckAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _currentPage?.OnHide();
        _state.Settings.WindowWidth = Width;
        _state.Settings.WindowHeight = Height;
        _state.Settings.WindowLeft = Left;
        _state.Settings.WindowTop = Top;
        _state.SaveSettings();
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _state.Settings.HideWhenMinimized)
        {
            Hide();
        }
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

        foreach (string page in new[] { "Processes", "Performance", "Startup apps", "Users", "Details", "Services", "Settings" })
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

    private void RegisterPages()
    {
        AddPage("Processes", new ProcessesPage(_state));
        AddPage("Performance", new PerformancePage(_state));
        AddPage("Startup apps", new StartupPage(_state));
        AddPage("Users", new StubPage(_state, "Users", "TODO: WTS session/process rollup."));
        AddPage("Details", new DetailsPage(_state));
        AddPage("Services", new ServicesPage(_state));
        AddPage("Settings", new SettingsPage(_state));
    }

    private void AddPage(string key, PageBase page)
    {
        page.Visible = false;
        _pages[key] = page;
        _pageHost.Controls.Add(page);
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out PageBase? page))
        {
            key = "Processes";
            page = _pages[key];
        }

        _currentPage?.OnHide();
        if (_currentPage is not null)
        {
            _currentPage.Visible = false;
        }

        page.Visible = true;
        page.BringToFront();
        _currentPage = page;
        _state.Settings.LastPage = key;
        _title.Text = page.Title;
        SetSearchVisible(page.UsesSearch);
        _search.Text = "";
        page.OnShow();

        foreach ((string navKey, Button button) in _navButtons)
        {
            button.Font = navKey == key ? _navSelectedFont : Font;
        }
    }

    private void ApplySettings()
    {
        TopMost = _state.Settings.AlwaysOnTop;
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

        if (InvokeRequired && IsHandleCreated)
        {
            BeginInvoke(action);
            return;
        }

        action();
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
            _navSelectedFont.Dispose();
        }
    }
}
