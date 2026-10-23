using FastTaskMgr.Diagnostics;
using FastTaskMgr.UI.Controls;

namespace FastTaskMgr.App;

internal abstract class PageBase : UserControl
{
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private Func<Task>? _refreshAction;
    private bool _autoRefresh;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _active;
    private bool _suspended;

    protected PageBase(AppState state)
    {
        State = state;
        Dock = DockStyle.Fill;
        _refreshTimer.Tick += async (_, _) =>
        {
            // Holding Ctrl freezes live updates, like Task Manager.
            if ((ModifierKeys & Keys.Control) != Keys.Control)
            {
                await RefreshAsync();
            }
        };
        State.SettingsChanged += (_, _) => UpdateRefreshTimer();
    }

    protected AppState State { get; }

    /// <summary>True while this is the page shown in the main window.</summary>
    protected bool IsActive => _active;

    /// <summary>True while the main window is minimized or hidden in the tray.</summary>
    protected bool IsSuspended => _suspended;

    public abstract string Title { get; }
    public virtual bool UsesSearch => true;
    public virtual void ApplySearch(string searchText) { }

    /// <summary>Called by the main window when the page is navigated to.</summary>
    public void ActivatePage()
    {
        _active = true;
        UpdateRefreshTimer();
        OnShow();
    }

    /// <summary>Called by the main window when another page is navigated to.</summary>
    public void DeactivatePage()
    {
        _active = false;
        UpdateRefreshTimer();
        OnHide();
    }

    /// <summary>
    /// Pauses timed refreshes while the window is minimized or hidden in the tray, and
    /// catches up with one refresh when it comes back.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended)
        {
            return;
        }

        _suspended = suspended;
        UpdateRefreshTimer();
        if (!suspended && _active)
        {
            if (_autoRefresh)
            {
                _ = RefreshAsync();
            }

            OnResume();
        }
    }

    protected virtual void OnShow() { }
    protected virtual void OnHide() { }

    /// <summary>Called when the window comes back from minimized or the tray while this page is shown.</summary>
    protected virtual void OnResume() { }

    /// <summary>
    /// Registers the page's data load. With autoRefresh it also runs on the update-speed
    /// timer, but only while this page is the visible one.
    /// </summary>
    protected void SetRefreshAction(Func<Task> refresh, bool autoRefresh)
    {
        _refreshAction = refresh;
        _autoRefresh = autoRefresh;
        UpdateRefreshTimer();
    }

    /// <summary>
    /// Runs the registered refresh unless one is in flight. Failures are logged rather than
    /// surfacing as an unhandled exception on every tick.
    /// </summary>
    protected async Task RefreshAsync()
    {
        if (_refreshing || IsDisposed || _refreshAction is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                await _refreshAction();
            }
            while (_refreshPending && !IsDisposed);
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"{GetType().Name} refresh", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Refresh after a user action. Unlike timer ticks, a request that lands while a refresh
    /// is in flight is queued, so the result of the action is never missed.
    /// </summary>
    protected Task RequestRefreshAsync()
    {
        if (_refreshing)
        {
            _refreshPending = true;
            return Task.CompletedTask;
        }

        return RefreshAsync();
    }

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
            _ = State.Settings.TrySave();
        };
    }

    private void UpdateRefreshTimer()
    {
        int interval = State.Settings.UpdateSpeed.ToMilliseconds();
        if (!_active || _suspended || !_autoRefresh || interval <= 0)
        {
            _refreshTimer.Stop();
            return;
        }

        if (!_refreshTimer.Enabled || _refreshTimer.Interval != interval)
        {
            _refreshTimer.Stop();
            _refreshTimer.Interval = interval;
            _refreshTimer.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
