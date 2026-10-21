using FastTaskMgr.App;
using FastTaskMgr.Core.Processes;
using FastTaskMgr.UI.Controls;

namespace FastTaskMgr.UI.Pages;

internal sealed class ProcessesPage : PageBase
{
    private readonly VirtualTable<ProcessRow> _table;
    private IReadOnlyList<ProcessRow> _rows = [];
    private string _filter = "";

    public ProcessesPage(AppState state)
        : base(state)
    {
        _table = new VirtualTable<ProcessRow>([
            new("Name", 180, row => row.Name),
            new("Status", 120, row => row.Status),
            new("CPU", 78, row => FormatUtil.Percent(row.CpuPercent), row => row.CpuPercent),
            new("Memory", 105, row => FormatUtil.Bytes(row.WorkingSetBytes), row => row.WorkingSetBytes),
            new("Disk", 96, row => $"{FormatUtil.Bytes((long)row.DiskBytesPerSecond)}/s", row => row.DiskBytesPerSecond),
            new("PID", 72, row => row.ProcessId.ToString(), row => row.ProcessId),
            new("Threads", 78, row => row.ThreadCount.ToString(), row => row.ThreadCount),
            new("Handles", 82, row => row.HandleCount.ToString(), row => row.HandleCount),
            new("Path", 420, row => row.Path ?? "")
        ], row => row.ProcessId);
        BindTableSort("Processes", _table);
        _table.ContextMenuStrip = BuildContextMenu();
        _table.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && _table.SelectedRows.Count > 0)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                EndSelected();
            }
        };

        Controls.Add(_table);
        Controls.Add(BuildToolbar());
        SetRefreshAction(RefreshRowsAsync, autoRefresh: true);
    }

    public override string Title => "Processes";

    protected override async void OnShow() => await RefreshAsync();

    public override void ApplySearch(string searchText)
    {
        if (_filter == searchText)
        {
            return;
        }

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
        AddButton(toolbar, "Refresh", async () => await RequestRefreshAsync());
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
        menu.Items.Add("End process tree", null, (_, _) => EndSelectedTree());
        menu.Items.Add("Restart Explorer", null, (_, _) => RestartExplorer());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Go to details", null, (_, _) => WithSelected(row => State.RequestDetails(row.ProcessId), "Go to details"));
        menu.Items.Add("Open file location", null, (_, _) => WithSelected(ProcessActions.OpenFileLocation, "Open location"));
        menu.Items.Add("Properties", null, (_, _) => WithSelected(ProcessActions.ShowProperties, "Properties"));
        menu.Items.Add("Search online", null, (_, _) => WithSelected(ProcessActions.SearchOnline, "Search online"));
        return menu;
    }

    private async Task RefreshRowsAsync()
    {
        IReadOnlyList<ProcessRow> rows = await Task.Run(() => State.Processes.Sample());
        if (!IsDisposed)
        {
            _rows = rows;
            ApplyFilter();
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

    private void EndSelectedTree()
    {
        ProcessRow? row = _table.SelectedRow;
        if (row is null)
        {
            return;
        }

        DialogResult result = MessageBox.Show(
            this,
            $"End {row.Name} ({row.ProcessId}) and all processes it started?",
            "End process tree",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
        {
            return;
        }

        IReadOnlyList<int> failed = ProcessActions.EndProcessTree(row, _rows);
        if (failed.Count > 0)
        {
            MessageBox.Show(this, $"Could not end {failed.Count} process(es). Access may be denied.", "End process tree", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestartExplorer()
    {
        RunAction(ProcessActions.RestartExplorer, "Restart Explorer");
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
