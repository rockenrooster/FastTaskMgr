using System.Diagnostics;
using FastTaskMgr.Core.Native;

namespace FastTaskMgr.Core.Processes;

internal sealed class ProcessSampler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _descriptionsByPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, TimeSpan> _lastCpuTimes = [];
    private Dictionary<int, ulong> _lastIoBytes = [];
    private DateTime _lastSampleUtc = DateTime.UtcNow;

    public IReadOnlyList<ProcessRow> Sample(bool includeDetails = false)
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = Math.Max(0.001, (now - _lastSampleUtc).TotalSeconds);
            Dictionary<int, TimeSpan> nextCpuTimes = [];
            Dictionary<int, ulong> nextIoBytes = [];
            List<ProcessRow> rows = [];

            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    ProcessRow? row = TryReadProcess(process, elapsedSeconds, nextCpuTimes, nextIoBytes, includeDetails);
                    if (row is not null)
                    {
                        rows.Add(row);
                    }
                }
            }

            _lastCpuTimes = nextCpuTimes;
            _lastIoBytes = nextIoBytes;
            _lastSampleUtc = now;
            return rows;
        }
    }

    private ProcessRow? TryReadProcess(Process process, double elapsedSeconds, Dictionary<int, TimeSpan> nextCpuTimes, Dictionary<int, ulong> nextIoBytes, bool includeDetails)
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

        ulong ioBytes = NativeMethods.QueryProcessIoBytes(pid);
        nextIoBytes[pid] = ioBytes;
        double diskBytesPerSecond = _lastIoBytes.TryGetValue(pid, out ulong lastIoBytes) && ioBytes >= lastIoBytes
            ? (ioBytes - lastIoBytes) / elapsedSeconds
            : 0;

        string? path = NativeMethods.QueryProcessImagePath(pid);
        string description = "";
        string userName = "";
        string architecture = "";
        string priority = "";
        string affinity = "";
        if (includeDetails)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                description = DescriptionFor(path);
            }

            userName = NativeMethods.QueryProcessUserName(pid) ?? "";
            architecture = NativeMethods.QueryProcessArchitecture(pid);
            priority = NativeMethods.QueryPriority(pid);
            affinity = NativeMethods.QueryAffinity(pid);
        }

        bool hasWindow = Safe(() => process.MainWindowHandle != IntPtr.Zero, false);
        bool responding = !hasWindow || Safe(() => process.Responding, true);

        return new ProcessRow(
            pid,
            null,
            name,
            responding ? "Running" : "Not responding",
            Math.Min(100, cpuPercent),
            Safe(() => process.WorkingSet64, 0),
            diskBytesPerSecond,
            Safe(() => process.Threads.Count, 0),
            Safe(() => process.HandleCount, 0),
            path,
            userName,
            architecture,
            description,
            priority,
            affinity);
    }

    private string DescriptionFor(string path)
    {
        if (_descriptionsByPath.TryGetValue(path, out string? description))
        {
            return description;
        }

        description = Safe(() => FileVersionInfo.GetVersionInfo(path).FileDescription ?? "", "");
        // ponytail: clear-on-churn beats an LRU until path churn is actually measurable.
        if (_descriptionsByPath.Count > 2048)
        {
            _descriptionsByPath.Clear();
        }

        _descriptionsByPath[path] = description;
        return description;
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
