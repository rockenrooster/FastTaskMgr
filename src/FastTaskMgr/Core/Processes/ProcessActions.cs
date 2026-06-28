using System.Diagnostics;
using FastTaskMgr.Core.Native;

namespace FastTaskMgr.Core.Processes;

internal static class ProcessActions
{
    public static readonly HashSet<string> CriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Idle",
        "Registry",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass"
    };

    public static bool EndProcess(ProcessRow row) => NativeMethods.TerminateProcessById(row.ProcessId);

    public static void RestartExplorer()
    {
        foreach (Process process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                _ = NativeMethods.TerminateProcessById(process.Id);
            }
        }

        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }

    public static void OpenFileLocation(ProcessRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Path}\"") { UseShellExecute = true });
    }

    public static void ShowProperties(ProcessRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true, Verb = "properties" });
    }

    public static void SearchOnline(ProcessRow row)
    {
        string query = Uri.EscapeDataString(row.Name);
        Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={query}") { UseShellExecute = true });
    }
}
