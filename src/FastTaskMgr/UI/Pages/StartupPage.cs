using FastTaskMgr.App;
using FastTaskMgr.Core.Startup;
using FastTaskMgr.UI.Controls;

namespace FastTaskMgr.UI.Pages;

internal sealed class StartupPage : PageBase
{
    private readonly VirtualTable<StartupItem> _table;
    private IReadOnlyList<StartupItem> _rows = [];
    private string _filter = "";

    public StartupPage(AppState state)
        : base(state)
    {
        _table = new VirtualTable<StartupItem>([
            new("Name", 210, row => row.Name),
            new("Publisher", 180, row => row.Publisher),
            new("Status", 90, row => row.Enabled ? "Enabled" : "Disabled", row => row.Enabled ? 1 : 0),
            new("Command", 420, row => row.Command),
            new("Source", 260, row => row.Source)
        ]);

        Controls.Add(_table);
        Controls.Add(BuildToolbar());
    }

    public override string Title => "Startup apps";

    public override void OnShow() => RefreshRows();

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

        AddButton(toolbar, "Enable", () => ToggleSelected(true));
        AddButton(toolbar, "Disable", () => ToggleSelected(false));
        AddButton(toolbar, "Open location", OpenSelectedLocation);
        AddButton(toolbar, "Properties", ShowSelectedProperties);
        AddButton(toolbar, "Search online", SearchSelectedOnline);
        AddButton(toolbar, "Refresh", RefreshRows);
        return toolbar;
    }

    private static void AddButton(FlowLayoutPanel panel, string text, Action action)
    {
        Button button = new() { Text = text, AutoSize = true, Height = 30 };
        button.Click += (_, _) => action();
        panel.Controls.Add(button);
    }

    private void RefreshRows()
    {
        _rows = State.Startup.ListStartupItems();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<StartupItem> rows = _rows;
        if (!string.IsNullOrWhiteSpace(_filter))
        {
            rows = rows.Where(row =>
                row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || row.Publisher.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || row.Command.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || row.Source.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        }

        _table.SetRows(rows);
    }

    private void ToggleSelected(bool enabled)
    {
        StartupItem? item = _table.SelectedRow;
        if (item is null || item.Enabled == enabled)
        {
            return;
        }

        try
        {
            State.Startup.SetEnabled(item, enabled);
            RefreshRows();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Startup apps", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedLocation()
    {
        StartupItem? item = _table.SelectedRow;
        if (item?.FilePath is null)
        {
            return;
        }

        RunAction(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true }), "Open location");
    }

    private void ShowSelectedProperties()
    {
        StartupItem? item = _table.SelectedRow;
        if (item?.FilePath is null)
        {
            return;
        }

        RunAction(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FilePath) { UseShellExecute = true, Verb = "properties" }), "Properties");
    }

    private void SearchSelectedOnline()
    {
        StartupItem? item = _table.SelectedRow;
        if (item is null)
        {
            return;
        }

        string query = Uri.EscapeDataString(item.Name);
        RunAction(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://www.bing.com/search?q={query}") { UseShellExecute = true }), "Search online");
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
