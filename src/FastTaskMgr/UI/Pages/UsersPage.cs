using FastTaskMgr.App;
using FastTaskMgr.Core.Processes;
using FastTaskMgr.Core.Users;
using FastTaskMgr.UI.Controls;

namespace FastTaskMgr.UI.Pages;

internal sealed class UsersPage : PageBase
{
    private readonly UserSessionRepository _repository = new();
    private readonly VirtualTable<UserSessionRow> _table;
    private IReadOnlyList<UserSessionRow> _rows = [];
    private string _filter = "";

    public UsersPage(AppState state)
        : base(state)
    {
        _table = new VirtualTable<UserSessionRow>([
            new("User", 240, row => row.UserName),
            new("Session", 84, row => row.SessionId.ToString(), row => row.SessionId),
            new("Status", 130, row => row.State),
            new("Processes", 92, row => row.ProcessCount.ToString(), row => row.ProcessCount),
            new("CPU", 78, row => FormatUtil.Percent(row.CpuPercent), row => row.CpuPercent),
            new("Memory", 105, row => FormatUtil.Bytes(row.MemoryBytes), row => row.MemoryBytes)
        ], row => row.SessionId);
        BindTableSort("Users", _table);

        Controls.Add(_table);
        Controls.Add(BuildToolbar());
        SetRefreshAction(RefreshRowsAsync, autoRefresh: true);
    }

    public override string Title => "Users";

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

        AddButton(toolbar, "Disconnect", () => WithSelected(Disconnect));
        AddButton(toolbar, "Sign off", () => WithSelected(SignOff));
        AddButton(toolbar, "Refresh", async () => await RequestRefreshAsync());
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
        IReadOnlyList<UserSessionRow> rows = await Task.Run(BuildRows);
        if (!IsDisposed)
        {
            _rows = rows;
            ApplyFilter();
        }
    }

    private IReadOnlyList<UserSessionRow> BuildRows()
    {
        IReadOnlyList<WtsSession> sessions = _repository.ListSessions();
        IReadOnlyList<ProcessRow> processes = State.Processes.Sample();
        Dictionary<int, (int Count, double Cpu, long Memory)> rollup = [];
        foreach (ProcessRow process in processes)
        {
            (int count, double cpu, long memory) = rollup.GetValueOrDefault(process.SessionId);
            rollup[process.SessionId] = (count + 1, cpu + process.CpuPercent, memory + process.WorkingSetBytes);
        }

        List<UserSessionRow> rows = new(sessions.Count);
        foreach (WtsSession session in sessions)
        {
            (int count, double cpu, long memory) = rollup.GetValueOrDefault(session.SessionId);
            rows.Add(new UserSessionRow(
                session.UserName,
                session.SessionId,
                session.State,
                count,
                Math.Min(100, cpu),
                memory));
        }

        return rows;
    }

    private void ApplyFilter()
    {
        IEnumerable<UserSessionRow> rows = _rows;
        if (!string.IsNullOrWhiteSpace(_filter))
        {
            rows = rows.Where(row => row.UserName.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        }

        _table.SetRows(rows);
    }

    private void Disconnect(UserSessionRow row)
    {
        if (Confirm($"Disconnect {row.UserName} (session {row.SessionId})?", "Disconnect"))
        {
            RunAction(() => _repository.Disconnect(row.SessionId), "Disconnect");
        }
    }

    private void SignOff(UserSessionRow row)
    {
        if (Confirm($"Sign off {row.UserName} (session {row.SessionId})? Unsaved work will be lost.", "Sign off"))
        {
            RunAction(() => _repository.Logoff(row.SessionId), "Sign off");
        }
    }

    private bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    private async void RunAction(Action action, string title)
    {
        try
        {
            await Task.Run(action);
            await RequestRefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void WithSelected(Action<UserSessionRow> action)
    {
        UserSessionRow? row = _table.SelectedRow;
        if (row is not null)
        {
            action(row);
        }
    }
}
