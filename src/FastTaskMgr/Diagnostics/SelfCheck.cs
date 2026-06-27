using FastTaskMgr.App;

namespace FastTaskMgr.Diagnostics;

internal static class SelfCheck
{
    public static int Run()
    {
        try
        {
            using AppState state = AppState.Load();
            bool hasProcesses = state.Processes.Sample().Count > 0;
            bool hasMemory = state.Performance.Sample().TotalMemoryBytes > 0;
            _ = state.Services.ListServices();
            _ = state.Startup.ListStartupItems();
            return hasProcesses && hasMemory ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
