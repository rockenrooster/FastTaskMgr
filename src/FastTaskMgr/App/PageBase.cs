namespace FastTaskMgr.App;

internal abstract class PageBase : UserControl
{
    protected PageBase(AppState state)
    {
        State = state;
        Dock = DockStyle.Fill;
    }

    protected AppState State { get; }
    public abstract string Title { get; }
    public virtual bool UsesSearch => true;
    public virtual void OnShow() { }
    public virtual void OnHide() { }
    public virtual void ApplySearch(string searchText) { }
}
