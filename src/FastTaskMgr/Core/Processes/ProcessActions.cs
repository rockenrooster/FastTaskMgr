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

    public static bool EndProcess(ProcessRow row) => NativeMethods.TerminateProcess(row.ProcessId, row.CreateTime);

    /// <summary>Terminates the process and every live descendant. Returns the PIDs that could not be ended.</summary>
    public static IReadOnlyList<int> EndProcessTree(ProcessRow target, IReadOnlyList<ProcessRow> allRows)
    {
        Dictionary<int, List<ProcessRow>> childrenByParent = [];
        foreach (ProcessRow row in allRows)
        {
            if (row.ProcessId == 0 || row.ParentProcessId == row.ProcessId)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(row.ParentProcessId, out List<ProcessRow>? children))
            {
                children = [];
                childrenByParent[row.ParentProcessId] = children;
            }

            children.Add(row);
        }

        List<int> failed = [];
        HashSet<int> visited = [];
        Queue<ProcessRow> pending = new();
        pending.Enqueue(target);
        while (pending.Count > 0)
        {
            ProcessRow process = pending.Dequeue();
            if (process.ProcessId <= 0 || !visited.Add(process.ProcessId))
            {
                continue;
            }

            if (childrenByParent.TryGetValue(process.ProcessId, out List<ProcessRow>? children))
            {
                foreach (ProcessRow child in children)
                {
                    // Parent PIDs are never cleared when a parent exits, so a "child" created
                    // before this process belongs to an earlier owner of the recycled PID.
                    if (child.CreateTime >= process.CreateTime)
                    {
                        pending.Enqueue(child);
                    }
                }
            }

            if (!NativeMethods.TerminateProcess(process.ProcessId, process.CreateTime))
            {
                failed.Add(process.ProcessId);
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
