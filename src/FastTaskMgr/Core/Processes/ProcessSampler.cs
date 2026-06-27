using System.Diagnostics;
using FastTaskMgr.Core.Native;

namespace FastTaskMgr.Core.Processes;

internal sealed class ProcessSampler
{
    private readonly Lock _lock = new();
    private Dictionary<int, TimeSpan> _lastCpuTimes = [];
    private DateTime _lastSampleUtc = DateTime.UtcNow;

    public IReadOnlyList<ProcessRow> Sample()
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = Math.Max(0.001, (now - _lastSampleUtc).TotalSeconds);
            Dictionary<int, TimeSpan> nextCpuTimes = [];
            List<ProcessRow> rows = [];

            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    ProcessRow? row = TryReadProcess(process, elapsedSeconds, nextCpuTimes);
                    if (row is not null)
                    {
                        rows.Add(row);
                    }
                }
            }

            _lastCpuTimes = nextCpuTimes;
            _lastSampleUtc = now;
            return rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.ProcessId).ToArray();
        }
    }

    private ProcessRow? TryReadProcess(Process process, double elapsedSeconds, Dictionary<int, TimeSpan> nextCpuTimes)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch
        {
            return null;
        }

        string name = Safe(() => process.ProcessName, $"PID {pid}");
        TimeSpan totalCpu = Safe(() => process.TotalProcessorTime, TimeSpan.Zero);
        nextCpuTimes[pid] = totalCpu;

        double cpuPercent = 0;
        if (_lastCpuTimes.TryGetValue(pid, out TimeSpan lastCpu))
        {
            cpuPercent = Math.Max(0, (totalCpu - lastCpu).TotalMilliseconds / (elapsedSeconds * 1000 * Environment.ProcessorCount) * 100);
        }

        string? path = NativeMethods.QueryProcessImagePath(pid);
        string description = "";
        if (!string.IsNullOrWhiteSpace(path))
        {
            description = Safe(() => FileVersionInfo.GetVersionInfo(path).FileDescription ?? "", "");
        }

        bool hasWindow = Safe(() => process.MainWindowHandle != IntPtr.Zero, false);
        bool responding = !hasWindow || Safe(() => process.Responding, true);

        return new ProcessRow(
            pid,
            NativeMethods.QueryParentProcessId(pid),
            name,
            responding ? "Running" : "Not responding",
            Math.Min(100, cpuPercent),
            Safe(() => process.WorkingSet64, 0),
            Safe(() => process.Threads.Count, 0),
            Safe(() => process.HandleCount, 0),
            path,
            NativeMethods.QueryProcessUserName(pid) ?? "",
            NativeMethods.QueryProcessArchitecture(pid),
            description,
            NativeMethods.QueryPriority(pid),
            NativeMethods.QueryAffinity(pid));
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }
}
