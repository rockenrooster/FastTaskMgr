namespace FastTaskMgr.App;

internal interface IAppPage
{
    string Title { get; }
    bool UsesSearch { get; }
    void OnShow();
    void OnHide();
    void ApplySearch(string searchText);
}

internal abstract class PageBase : UserControl, IAppPage
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
