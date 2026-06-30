using FastTaskMgr.UI.Controls;

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

    protected void BindTableSort<T>(string key, VirtualTable<T> table)
        where T : class
    {
        if (State.Settings.TableSorts.TryGetValue(key, out TableSortState? sort))
        {
            table.SetSort(sort.Column, sort.Descending);
        }

        table.SortChanged += (_, _) =>
        {
            State.Settings.TableSorts[key] = new TableSortState
            {
                Column = table.SortColumnTitle,
                Descending = table.SortDescending
            };
            State.Settings.Save();
        };
    }
}
