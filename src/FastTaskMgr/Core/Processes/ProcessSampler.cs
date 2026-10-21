using System.Diagnostics;
using FastTaskMgr.Core.Native;

namespace FastTaskMgr.Core.Processes;

internal sealed class ProcessSampler : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SystemProcessReader _reader = new();
    private readonly List<SystemProcessEntry> _entries = new(512);
    private readonly Dictionary<string, FileInfoEntry> _fileInfoByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ProcessKey, ProcessIdentity> _identities = [];
    // Two counter maps swapped every sample instead of allocating a new one per tick.
    private Dictionary<ProcessKey, CounterSample> _lastCounters = [];
    private Dictionary<ProcessKey, CounterSample> _nextCounters = [];
    private long _lastTimestamp;
    private bool _disposed;

    private readonly record struct ProcessKey(int ProcessId, long CreateTime);
    private readonly record struct CounterSample(ulong CpuTime100ns, ulong IoBytes);
    private sealed record FileInfoEntry(string Description, string Company);

    // Immutable per process lifetime, so resolved once per (pid, create time) instead of per tick.
    private sealed class ProcessIdentity
    {
        public string? Name;
        public string? Path;
        public string? UserName;
        public string Architecture = "";
        public string CommandLine = "";
        public bool PathResolved;
        public bool DetailsResolved;
    }

    public IReadOnlyList<ProcessRow> Sample(bool includeDetails = false)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reader.Read(_entries);
            HashSet<int> hungPids = WindowSnapshot.QueryHungProcessIds();

            long now = Stopwatch.GetTimestamp();
            double elapsedSeconds = _lastTimestamp == 0
                ? 0
                : Math.Max(0.001, (now - _lastTimestamp) / (double)Stopwatch.Frequency);
            _lastTimestamp = now;
            // All logical processors, not Environment.ProcessorCount: that one follows this
            // process's own affinity/job limits and would inflate every percentage.
            double cpuCapacity100ns = elapsedSeconds * 10_000_000d * NativeMethods.LogicalProcessorCount;

            Dictionary<ProcessKey, CounterSample> nextCounters = _nextCounters;
            nextCounters.Clear();
            List<ProcessRow> rows = new(_entries.Count);
            foreach (SystemProcessEntry entry in _entries)
            {
                ProcessKey key = new(entry.ProcessId, entry.CreateTime);
                nextCounters[key] = new CounterSample(entry.CpuTime100ns, entry.IoTransferBytes);

                double cpuPercent = 0;
                double diskBytesPerSecond = 0;
                if (elapsedSeconds > 0 && _lastCounters.TryGetValue(key, out CounterSample last))
                {
                    if (entry.CpuTime100ns >= last.CpuTime100ns)
                    {
                        cpuPercent = Math.Min(100, (entry.CpuTime100ns - last.CpuTime100ns) / cpuCapacity100ns * 100);
                    }

                    if (entry.IoTransferBytes >= last.IoBytes)
                    {
                        diskBytesPerSecond = (entry.IoTransferBytes - last.IoBytes) / elapsedSeconds;
                    }
                }

                rows.Add(BuildRow(entry, key, cpuPercent, diskBytesPerSecond, hungPids, includeDetails));
            }

            _nextCounters = _lastCounters;
            _lastCounters = nextCounters;
            PruneIdentities(nextCounters);
            return rows;
        }
    }

    private ProcessRow BuildRow(
        in SystemProcessEntry entry,
        ProcessKey key,
        double cpuPercent,
        double diskBytesPerSecond,
        HashSet<int> hungPids,
        bool includeDetails)
    {
        if (!_identities.TryGetValue(key, out ProcessIdentity? identity))
        {
            identity = new ProcessIdentity();
            _identities[key] = identity;
        }

        identity.Name ??= _reader.ReadName(entry);
        string affinity = "";
        bool canOpen = entry.ProcessId > 0;
        bool needsStatic = canOpen && (!identity.PathResolved || (includeDetails && !identity.DetailsResolved));
        if (needsStatic || (canOpen && includeDetails))
        {
            using SafeNativeHandle process = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation, false, entry.ProcessId);
            if (!identity.PathResolved)
            {
                identity.Path = NativeMethods.QueryProcessImagePath(process);
                identity.PathResolved = true;
            }

            if (includeDetails)
            {
                if (!identity.DetailsResolved)
                {
                    identity.UserName = NativeMethods.QueryProcessUserName(process);
                    identity.Architecture = NativeMethods.QueryProcessArchitecture(process);
                    identity.CommandLine = NativeMethods.QueryProcessCommandLine(process) ?? "";
                    identity.DetailsResolved = true;
                }

                affinity = NativeMethods.QueryAffinity(process);
            }
        }

        string description = "";
        string company = "";
        if (includeDetails && identity.Path is { Length: > 0 })
        {
            FileInfoEntry info = FileInfoFor(identity.Path);
            description = info.Description;
            company = info.Company;
        }

        string status = entry.IsSuspended
            ? "Suspended"
            : hungPids.Contains(entry.ProcessId) ? "Not responding" : "Running";

        return new ProcessRow(
            entry.ProcessId,
            entry.ParentProcessId,
            identity.Name,
            status,
            cpuPercent,
            entry.WorkingSetBytes,
            diskBytesPerSecond,
            entry.ThreadCount,
            entry.HandleCount,
            entry.SessionId,
            identity.Path,
            identity.UserName,
            identity.Architecture,
            description,
            company,
            identity.CommandLine,
            PriorityName(entry.BasePriority),
            affinity,
            entry.CreateTime);
    }

    private void PruneIdentities(Dictionary<ProcessKey, CounterSample> liveKeys)
    {
        if (_identities.Count <= liveKeys.Count)
        {
            return;
        }

        List<ProcessKey> dead = [];
        foreach (ProcessKey key in _identities.Keys)
        {
            if (!liveKeys.ContainsKey(key))
            {
                dead.Add(key);
            }
        }

        foreach (ProcessKey key in dead)
        {
            _identities.Remove(key);
        }
    }

    private FileInfoEntry FileInfoFor(string path)
    {
        if (_fileInfoByPath.TryGetValue(path, out FileInfoEntry? info))
        {
            return info;
        }

        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            info = new FileInfoEntry(version.FileDescription ?? "", version.CompanyName ?? "");
        }
        catch
        {
            info = new FileInfoEntry("", "");
        }

        // ponytail: clear-on-churn beats an LRU until path churn is actually measurable.
        if (_fileInfoByPath.Count > 2048)
        {
            _fileInfoByPath.Clear();
        }

        _fileInfoByPath[path] = info;
        return info;
    }

    // Base priority is reported by the snapshot, so the priority class costs no extra handle opens.
    private static string PriorityName(int basePriority) => basePriority switch
    {
        4 => "Idle",
        6 => "Below normal",
        8 => "Normal",
        10 => "Above normal",
        13 => "High",
        24 => "Realtime",
        _ => basePriority.ToString()
    };

    // Waits for an in-flight background sample, so the snapshot buffer is never released under it.
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _reader.Dispose();
        }
    }
}
