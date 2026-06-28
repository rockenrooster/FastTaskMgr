using FastTaskMgr.App;
using FastTaskMgr.Core.Native;
using FastTaskMgr.Core.Processes;
using FastTaskMgr.UI.Controls;

namespace FastTaskMgr.UI.Pages;

internal sealed class DetailsPage : PageBase
{
    private readonly VirtualTable<ProcessRow> _table;
    private readonly System.Windows.Forms.Timer _timer = new();
    private IReadOnlyList<ProcessRow> _rows = [];
    private string _filter = "";
    private bool _refreshing;

    public DetailsPage(AppState state)
        : base(state)
    {
        _table = new VirtualTable<ProcessRow>([
            new("Name", 170, row => row.Name),
            new("PID", 72, row => row.ProcessId.ToString(), row => row.ProcessId),
            new("Status", 112, row => row.Status),
            new("User", 170, row => row.UserName ?? ""),
            new("CPU", 72, row => FormatUtil.Percent(row.CpuPercent), row => row.CpuPercent),
            new("Memory", 102, row => FormatUtil.Bytes(row.WorkingSetBytes), row => row.WorkingSetBytes),
            new("Arch", 70, row => row.Architecture),
            new("Priority", 105, row => row.Priority),
            new("Affinity", 92, row => row.Affinity),
            new("Description", 230, row => row.Description),
            new("Path", 420, row => row.Path ?? "")
        ], row => row.ProcessId);
        _table.ContextMenuStrip = BuildContextMenu();

        Controls.Add(_table);
        Controls.Add(BuildToolbar());
        _timer.Tick += async (_, _) => await RefreshRowsAsync();
        State.SettingsChanged += (_, _) => ConfigureTimer();
    }

    public override string Title => "Details";

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

        AddButton(toolbar, "End process", EndSelected, danger: true);
        AddButton(toolbar, "Open location", () => WithSelected(ProcessActions.OpenFileLocation, "Open location"));
        AddButton(toolbar, "Properties", () => WithSelected(ProcessActions.ShowProperties, "Properties"));
        AddButton(toolbar, "Search online", () => WithSelected(ProcessActions.SearchOnline, "Search online"));

        ComboBox priority = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 130
        };
        priority.Items.AddRange(["Idle", "Below normal", "Normal", "Above normal", "High", "Realtime"]);
        priority.SelectedItem = "Normal";
        toolbar.Controls.Add(priority);
        AddButton(toolbar, "Set priority", () => SetPriority(priority.Text));
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
        menu.Items.Add("End process", null, (_, _) => EndSelected());

        ToolStripMenuItem priority = new("Set priority");
        foreach (string value in new[] { "Idle", "Below normal", "Normal", "Above normal", "High", "Realtime" })
        {
            priority.DropDownItems.Add(value, null, (_, _) => SetPriority(value));
        }

        menu.Items.Add(priority);
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
            IReadOnlyList<ProcessRow> rows = await Task.Run(() => State.Processes.Sample(includeDetails: true));
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
                || (row.UserName?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || row.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase)
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

        if (State.Settings.ConfirmBeforeEndProcess)
        {
            string target = rows.Count == 1
                ? $"{rows[0].Name} ({rows[0].ProcessId})"
                : $"{rows.Count} processes";
            DialogResult result = MessageBox.Show(this, $"End {target}?", "End process", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes)
            {
                return;
            }
        }

        foreach (ProcessRow row in rows)
        {
            if (!ProcessActions.EndProcess(row))
            {
                MessageBox.Show(this, $"Could not end {row.Name}. Access may be denied.", "End process", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void SetPriority(string priority)
    {
        IReadOnlyList<ProcessRow> rows = _table.SelectedRows;
        if (rows.Count == 0)
        {
            return;
        }

        if (priority == "Realtime")
        {
            DialogResult result = MessageBox.Show(this, "Realtime priority can make the system unstable. Continue?", "Set priority", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes)
            {
                return;
            }
        }

        try
        {
            foreach (ProcessRow row in rows)
            {
                NativeMethods.SetPriority(row.ProcessId, PriorityValue(priority));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Set priority", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static uint PriorityValue(string priority) => priority switch
    {
        "Idle" => NativeMethods.PriorityIdle,
        "Below normal" => NativeMethods.PriorityBelowNormal,
        "Above normal" => NativeMethods.PriorityAboveNormal,
        "High" => NativeMethods.PriorityHigh,
        "Realtime" => NativeMethods.PriorityRealtime,
        _ => NativeMethods.PriorityNormal
    };

    private void WithSelected(Action<ProcessRow> action, string title)
    {
        ProcessRow? row = _table.SelectedRow;
        if (row is not null)
        {
            try
            {
                action(row);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
