using FastTaskMgr.Core.Performance;
using FastTaskMgr.Core.Processes;
using FastTaskMgr.Core.Services;
using FastTaskMgr.Core.Startup;
using FastTaskMgr.Core.Updates;

namespace FastTaskMgr.App;

internal sealed class AppState : IDisposable
{
    private AppState(AppSettings settings)
    {
        Settings = settings;
        Processes = new ProcessSampler();
        Performance = new PerformanceSampler();
        Services = new ServiceRepository();
        Startup = new StartupRepository();
        Updates = new UpdateService();
    }

    public AppSettings Settings { get; }
    public ProcessSampler Processes { get; }
    public PerformanceSampler Performance { get; }
    public ServiceRepository Services { get; }
    public StartupRepository Startup { get; }
    public UpdateService Updates { get; }

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
        Performance.Dispose();
        Updates.Dispose();
    }
}
