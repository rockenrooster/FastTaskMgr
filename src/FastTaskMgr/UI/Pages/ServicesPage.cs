using System.Diagnostics;
using FastTaskMgr.App;
using FastTaskMgr.Core.Services;
using FastTaskMgr.UI.Controls;

namespace FastTaskMgr.UI.Pages;

internal sealed class ServicesPage : PageBase
{
    private readonly VirtualTable<ServiceRow> _table;
    private IReadOnlyList<ServiceRow> _rows = [];
    private string _filter = "";
    private bool _refreshing;

    public ServicesPage(AppState state)
        : base(state)
    {
        _table = new VirtualTable<ServiceRow>([
            new("Name", 180, row => row.Name),
            new("PID", 74, row => row.ProcessId == 0 ? "" : row.ProcessId.ToString(), row => row.ProcessId),
            new("Description", 360, row => row.Description),
            new("Status", 120, row => row.Status),
            new("Display name", 260, row => row.DisplayName),
            new("Group", 120, row => row.Group)
        ]);
        BindTableSort("Services", _table);

        Controls.Add(_table);
        Controls.Add(BuildToolbar());
    }

    public override string Title => "Services";

    public override async void OnShow() => await RefreshRowsAsync();

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

        AddButton(toolbar, "Start", () => WithSelected(row => RunServiceAction(() => State.Services.Start(row.Name))));
        AddButton(toolbar, "Stop", () => WithSelected(row => RunServiceAction(() => State.Services.Stop(row.Name))));
        AddButton(toolbar, "Restart", RestartSelected);
        AddButton(toolbar, "Open Services", OpenServicesConsole);
        AddButton(toolbar, "Search online", () => WithSelected(row => Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={Uri.EscapeDataString(row.Name)}") { UseShellExecute = true })));
        AddButton(toolbar, "Refresh", async () => await RefreshRowsAsync());
        return toolbar;
    }

    private static void AddButton(FlowLayoutPanel panel, string text, Action action)
    {
        Button button = new() { Text = text, AutoSize = true, Height = 30 };
        button.Click += (_, _) => action();
        panel.Controls.Add(button);
    }

    private async Task RefreshRowsAsync()
    {
        if (_refreshing || IsDisposed)
        {
            return;
        }

        _refreshing = true;
        try
        {
            IReadOnlyList<ServiceRow> rows = await Task.Run(State.Services.ListServices);
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

    private void ApplyFilter()
    {
        IEnumerable<ServiceRow> rows = _rows;
        if (!string.IsNullOrWhiteSpace(_filter))
        {
            rows = rows.Where(row =>
                row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || row.DisplayName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || row.Status.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        }

        _table.SetRows(rows);
    }

    private void RestartSelected()
    {
        WithSelected(row => RunServiceAction(() =>
        {
            State.Services.Stop(row.Name);
            State.Services.Start(row.Name);
        }));
    }

    private async void RunServiceAction(Action action)
    {
        try
        {
            await Task.Run(action);
            await RefreshRowsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Service action", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenServicesConsole()
    {
        Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
    }

    private void WithSelected(Action<ServiceRow> action)
    {
        ServiceRow? row = _table.SelectedRow;
        if (row is not null)
        {
            action(row);
        }
    }
}
