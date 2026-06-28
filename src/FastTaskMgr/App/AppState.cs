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

    public event EventHandler? SettingsChanged;

    public static AppState Load() => new(AppSettings.Load());

    public void SaveSettings()
    {
        Settings.Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Settings.Save();
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
