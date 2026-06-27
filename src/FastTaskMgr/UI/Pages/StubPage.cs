using FastTaskMgr.App;

namespace FastTaskMgr.UI.Pages;

internal sealed class StubPage : PageBase
{
    private readonly string _title;

    public StubPage(AppState state, string title, string todo)
        : base(state)
    {
        _title = title;
        Label label = new()
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(14),
            Text = todo
        };
        Controls.Add(label);
    }

    public override string Title => _title;
}
