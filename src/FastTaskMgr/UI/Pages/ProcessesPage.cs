using FastTaskMgr.App;
using FastTaskMgr.Core.Processes;
using FastTaskMgr.UI.Controls;

namespace FastTaskMgr.UI.Pages;

internal sealed class ProcessesPage : PageBase
{
    private readonly VirtualTable<ProcessRow> _table;
    private readonly System.Windows.Forms.Timer _timer = new();
    private IReadOnlyList<ProcessRow> _rows = [];
    private string _filter = "";
    private bool _refreshing;

    public ProcessesPage(AppState state)
        : base(state)
    {
        _table = new VirtualTable<ProcessRow>([
            new("Name", 180, row => row.Name),
            new("PID", 72, row => row.ProcessId.ToString(), row => row.ProcessId),
            new("Status", 120, row => row.Status),
            new("CPU", 78, row => FormatUtil.Percent(row.CpuPercent), row => row.CpuPercent),
            new("Memory", 105, row => FormatUtil.Bytes(row.WorkingSetBytes), row => row.WorkingSetBytes),
            new("Threads", 78, row => row.ThreadCount.ToString(), row => row.ThreadCount),
            new("Handles", 82, row => row.HandleCount.ToString(), row => row.HandleCount),
            new("Path", 420, row => row.Path ?? "")
        ], row => row.ProcessId);
        _table.ContextMenuStrip = BuildContextMenu();

        Controls.Add(_table);
        Controls.Add(BuildToolbar());
        _timer.Tick += async (_, _) => await RefreshRowsAsync();
        State.SettingsChanged += (_, _) => ConfigureTimer();
    }

    public override string Title => "Processes";

    public override async void OnShow()
    {
        ConfigureTimer();
        await RefreshRowsAsync();
    }

    public override void OnHide() => _timer.Stop();

    public override void ApplySearch(string searchText)
    {
        _filter = searchText;
        ApplyFilter();
    }

    private FlowLayoutPanel BuildToolbar()
    {
        FlowLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(8, 4, 8, 4),
            WrapContents = false
        };

        AddButton(toolbar, "End task", EndSelected, danger: true);
        AddButton(toolbar, "Restart Explorer", RestartExplorer);
        AddButton(toolbar, "Open location", () => WithSelected(ProcessActions.OpenFileLocation, "Open location"));
        AddButton(toolbar, "Properties", () => WithSelected(ProcessActions.ShowProperties, "Properties"));
        AddButton(toolbar, "Search online", () => WithSelected(ProcessActions.SearchOnline, "Search online"));
        AddButton(toolbar, "Refresh", async () => await RefreshRowsAsync());
        return toolbar;
    }

    private static void AddButton(FlowLayoutPanel panel, string text, Action action, bool danger = false)
    {
        Button button = new()
        {
            Text = text,
            AutoSize = true,
            Height = 30,
            BackColor = danger ? Color.FromArgb(122, 28, 28) : SystemColors.Control,
            ForeColor = danger ? Color.White : SystemColors.ControlText
        };
        button.Click += (_, _) => action();
        panel.Controls.Add(button);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        ContextMenuStrip menu = new();
        menu.Items.Add("End task", null, (_, _) => EndSelected());
        menu.Items.Add("Restart Explorer", null, (_, _) => RestartExplorer());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open file location", null, (_, _) => WithSelected(ProcessActions.OpenFileLocation, "Open location"));
        menu.Items.Add("Properties", null, (_, _) => WithSelected(ProcessActions.ShowProperties, "Properties"));
        menu.Items.Add("Search online", null, (_, _) => WithSelected(ProcessActions.SearchOnline, "Search online"));
        return menu;
    }

    private async Task RefreshRowsAsync()
    {
        if (_refreshing || IsDisposed || (ModifierKeys & Keys.Control) == Keys.Control)
        {
            return;
        }

        _refreshing = true;
        try
        {
            IReadOnlyList<ProcessRow> rows = await Task.Run(State.Processes.Sample);
            if (!IsDisposed)
            {
                _rows = rows;
                ApplyFilter();
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ConfigureTimer()
    {
        int interval = State.Settings.UpdateSpeed.ToMilliseconds();
        _timer.Stop();
        if (interval > 0)
        {
            _timer.Interval = interval;
            _timer.Start();
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<ProcessRow> rows = _rows;
        if (!string.IsNullOrWhiteSpace(_filter))
        {
            rows = rows.Where(row =>
                row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || row.ProcessId.ToString().Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || (row.Path?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        _table.SetRows(rows);
    }

    private void EndSelected()
    {
        IReadOnlyList<ProcessRow> rows = _table.SelectedRows;
        if (rows.Count == 0)
        {
            return;
        }

        bool hasCritical = rows.Any(row => ProcessActions.CriticalProcessNames.Contains(row.Name));
        if (State.Settings.ConfirmBeforeEndProcess || hasCritical)
        {
            string target = rows.Count == 1
                ? $"{rows[0].Name} ({rows[0].ProcessId})"
                : $"{rows.Count} processes";
            DialogResult result = MessageBox.Show(
                this,
                $"End {target}?",
                "End task",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes)
            {
                return;
            }
        }

        foreach (ProcessRow row in rows)
        {
            if (!ProcessActions.EndProcess(row))
            {
                MessageBox.Show(this, $"Could not end {row.Name}. Access may be denied.", "End task", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void RestartExplorer()
    {
        ProcessRow? row = _table.SelectedRow;
        if (row is null || !row.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Select explorer.exe first.", "Restart Explorer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        RunAction(() => ProcessActions.RestartExplorer(row), "Restart Explorer");
    }

    private void WithSelected(Action<ProcessRow> action, string title)
    {
        ProcessRow? row = _table.SelectedRow;
        if (row is not null)
        {
            RunAction(() => action(row), title);
        }
    }

    private void RunAction(Action action, string title)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
