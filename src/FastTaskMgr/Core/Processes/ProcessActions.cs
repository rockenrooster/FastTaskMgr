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

    /// <summary>Terminates the process and every live descendant. Returns the PIDs that could not be ended.</summary>
    public static IReadOnlyList<int> EndProcessTree(ProcessRow target, IReadOnlyList<ProcessRow> allRows)
    {
        Dictionary<int, List<int>> childrenByParent = [];
        foreach (ProcessRow row in allRows)
        {
            if (row.ProcessId == 0 || row.ParentProcessId == row.ProcessId)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(row.ParentProcessId, out List<int>? children))
            {
                children = [];
                childrenByParent[row.ParentProcessId] = children;
            }

            children.Add(row.ProcessId);
        }

        List<int> failed = [];
        HashSet<int> visited = [];
        Queue<int> pending = new();
        pending.Enqueue(target.ProcessId);
        while (pending.Count > 0)
        {
            int pid = pending.Dequeue();
            if (pid <= 0 || !visited.Add(pid))
            {
                continue;
            }

            if (childrenByParent.TryGetValue(pid, out List<int>? children))
            {
                foreach (int child in children)
                {
                    pending.Enqueue(child);
                }
            }

            if (!NativeMethods.TerminateProcessById(pid))
            {
                failed.Add(pid);
            }
        }

        return failed;
    }

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
