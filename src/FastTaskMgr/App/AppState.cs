using FastTaskMgr.Core.Performance;
using FastTaskMgr.Core.Processes;
using FastTaskMgr.Core.Services;
using FastTaskMgr.Core.Startup;
using FastTaskMgr.Core.Updates;

namespace FastTaskMgr.App;

internal sealed class AppState : IDisposable
{
    private readonly Lazy<ProcessSampler> _processes = new(() => new ProcessSampler());
    private readonly Lazy<PerformanceSampler> _performance = new(() => new PerformanceSampler());
    private readonly Lazy<ServiceRepository> _services = new(() => new ServiceRepository());
    private readonly Lazy<StartupRepository> _startup = new(() => new StartupRepository());
    private readonly Lazy<UpdateService> _updates = new(() => new UpdateService());

    private AppState(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; }
    public ProcessSampler Processes => _processes.Value;
    public PerformanceSampler Performance => _performance.Value;
    public ServiceRepository Services => _services.Value;
    public StartupRepository Startup => _startup.Value;
    public UpdateService Updates => _updates.Value;
    public bool IsUpdateAvailable => _updates.IsValueCreated && _updates.Value.LastResult?.IsUpdateAvailable == true;

    public event EventHandler? SettingsChanged;
    public event EventHandler<int>? DetailsNavigationRequested;

    public void RequestDetails(int processId) => DetailsNavigationRequested?.Invoke(this, processId);

    public static AppState Load() => new(AppSettings.Load());

    /// <summary>Persists settings and notifies listeners. Returns false when the file could not be written.</summary>
    public bool SaveSettings()
    {
        bool saved = Settings.TrySave();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    // Settings are persisted by the main window on close. The samplers wait for any
    // in-flight background sample before releasing native buffers and PDH handles.
    public void Dispose()
    {
        if (_processes.IsValueCreated)
        {
            _processes.Value.Dispose();
        }

        if (_performance.IsValueCreated)
        {
            _performance.Value.Dispose();
        }

        if (_updates.IsValueCreated)
        {
            _updates.Value.Dispose();
        }
    }
}
