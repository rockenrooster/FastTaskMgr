using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using FastTaskMgr.Core.Native;
using Microsoft.Win32;

namespace FastTaskMgr.Core.Performance;

internal sealed class PerformanceSampler : IDisposable
{
    private readonly Lock _lock = new();
    private readonly CpuInfo _cpuInfo = ReadCpuInfo();
    private readonly HashSet<string> _physicalAdapterIds = ReadPhysicalAdapterIds();
    private readonly Dictionary<string, PerformanceCounter> _diskActiveCounters = [];
    private readonly Dictionary<string, PerformanceCounter> _diskReadCounters = [];
    private readonly Dictionary<string, PerformanceCounter> _diskWriteCounters = [];
    private readonly Dictionary<string, (long sent, long received, DateTime time)> _lastNetworkBytes = [];
    private IReadOnlyList<PerformanceCounter>? _gpuUtilCounters;
    private IReadOnlyList<PerformanceCounter>? _gpuDedicatedCounters;
    private IReadOnlyList<PerformanceCounter>? _gpuSharedCounters;
    private bool _gpuCountersInitialized;
    private ProcessorPerformanceInformation[] _lastProcessorInfo = [];
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;

    public PerformanceSample Sample()
    {
        lock (_lock)
        {
            double cpuPercent = SampleCpu();
            double[] corePercents = SampleCpuCores();
            MemoryStatusEx memory = MemoryStatusEx.Create();
            _ = NativeMethods.GlobalMemoryStatusEx(ref memory);
            _ = NativeMethods.GetPerformanceInfo(out PerformanceInformation info, Marshal.SizeOf<PerformanceInformation>());
            ulong pageSize = info.PageSize;

            return new PerformanceSample(
                cpuPercent,
                corePercents,
                _cpuInfo,
                CurrentCpuSpeedGhz(),
                memory.ullTotalPhys,
                memory.ullAvailPhys,
                (ulong)info.CommitTotal * pageSize,
                (ulong)info.CommitLimit * pageSize,
                (ulong)info.SystemCache * pageSize,
                info.ProcessCount,
                info.ThreadCount,
                info.HandleCount,
                TimeSpan.FromMilliseconds(Environment.TickCount64),
                SampleDisks(),
                SampleNetworks(),
                SampleGpus());
        }
    }

    private double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime))
        {
            return 0;
        }

        ulong idle = idleTime.ToUInt64();
        ulong kernel = kernelTime.ToUInt64();
        ulong user = userTime.ToUInt64();

        ulong idleDelta = idle - _lastIdle;
        ulong kernelDelta = kernel - _lastKernel;
        ulong userDelta = user - _lastUser;
        ulong total = kernelDelta + userDelta;
        bool hasPrevious = _lastKernel != 0 || _lastUser != 0;

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        if (!hasPrevious || total == 0)
        {
            return 0;
        }

        return Math.Clamp((double)(total - idleDelta) / total * 100, 0, 100);
    }

    private double[] SampleCpuCores()
    {
        ProcessorPerformanceInformation[] current = NativeMethods.QueryProcessorPerformance();
        if (current.Length == 0)
        {
            return [];
        }

        double[] values = new double[current.Length];
        for (int index = 0; index < current.Length; index++)
        {
            if (index >= _lastProcessorInfo.Length)
            {
                continue;
            }

            ProcessorPerformanceInformation last = _lastProcessorInfo[index];
            long idle = current[index].IdleTime - last.IdleTime;
            long kernel = current[index].KernelTime - last.KernelTime;
            long user = current[index].UserTime - last.UserTime;
            long total = kernel + user;
            values[index] = total <= 0 ? 0 : Math.Clamp((double)(total - idle) / total * 100, 0, 100);
        }

        _lastProcessorInfo = current;
        return values;
    }

    private DiskPerformanceSample[] SampleDisks()
    {
        List<DiskPerformanceSample> disks = [];
        int index = 0;
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;
            long used = total - free;
            disks.Add(new DiskPerformanceSample(
                $"disk:{drive.Name}",
                $"Disk {index} ({drive.Name.TrimEnd('\\')})",
                drive.DriveFormat,
                ReadCounter(_diskActiveCounters, "% Disk Time", index.ToString()),
                ReadCounter(_diskReadCounters, "Disk Read Bytes/sec", index.ToString()),
                ReadCounter(_diskWriteCounters, "Disk Write Bytes/sec", index.ToString()),
                used,
                free,
                total));
            index++;
        }

        return disks.ToArray();
    }

    private static double ReadCounter(Dictionary<string, PerformanceCounter> counters, string counterName, string instance)
    {
        string key = counterName + "|" + instance;
        try
        {
            if (!counters.TryGetValue(key, out PerformanceCounter? counter))
            {
                counter = new PerformanceCounter("PhysicalDisk", counterName, instance, readOnly: true);
                _ = counter.NextValue();
                counters[key] = counter;
                return 0;
            }

            return Math.Max(0, counter.NextValue());
        }
        catch
        {
            return 0;
        }
    }

    private NetworkPerformanceSample[] SampleNetworks()
    {
        DateTime now = DateTime.UtcNow;
        List<NetworkPerformanceSample> networks = [];
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsPhysicalNic(adapter, _physicalAdapterIds))
            {
                continue;
            }

            IPv4InterfaceStatistics stats;
            try
            {
                stats = adapter.GetIPv4Statistics();
            }
            catch
            {
                continue;
            }

            string key = $"net:{adapter.Id}";
            double elapsed = 0;
            long sentDelta = 0;
            long receivedDelta = 0;
            if (_lastNetworkBytes.TryGetValue(key, out (long sent, long received, DateTime time) last))
            {
                elapsed = Math.Max(0.001, (now - last.time).TotalSeconds);
                sentDelta = Math.Max(0, stats.BytesSent - last.sent);
                receivedDelta = Math.Max(0, stats.BytesReceived - last.received);
            }

            _lastNetworkBytes[key] = (stats.BytesSent, stats.BytesReceived, now);
            double send = elapsed <= 0 ? 0 : sentDelta / elapsed;
            double receive = elapsed <= 0 ? 0 : receivedDelta / elapsed;
            double speedBytes = adapter.Speed > 0 ? adapter.Speed / 8d : 0;
            double utilization = speedBytes <= 0 ? 0 : Math.Min(100, (send + receive) / speedBytes * 100);

            networks.Add(new NetworkPerformanceSample(
                key,
                adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet",
                adapter.Name,
                IpAddress(adapter),
                adapter.Speed,
                send,
                receive,
                utilization));
        }

        return networks.ToArray();
    }

    private GpuPerformanceSample[] SampleGpus()
    {
        EnsureGpuCounters();
        if (_gpuUtilCounters is null || _gpuUtilCounters.Count == 0)
        {
            return [];
        }

        double utilization = 0;
        foreach (PerformanceCounter counter in _gpuUtilCounters)
        {
            utilization += ReadCounterValue(counter);
        }

        long dedicated = SumCounterBytes(_gpuDedicatedCounters);
        long shared = SumCounterBytes(_gpuSharedCounters);
        return
        [
            new GpuPerformanceSample(
                "gpu:0",
                "GPU 0",
                "Windows GPU Engine counters",
                Math.Clamp(utilization, 0, 100),
                dedicated,
                shared)
        ];
    }

    private void EnsureGpuCounters()
    {
        if (_gpuCountersInitialized)
        {
            return;
        }

        _gpuCountersInitialized = true;
        _gpuUtilCounters = CreateGpuCounters("GPU Engine", "Utilization Percentage", instance => instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase));
        if (_gpuUtilCounters.Count == 0)
        {
            _gpuUtilCounters = CreateGpuCounters("GPU Engine", "Utilization Percentage", _ => true);
        }

        _gpuDedicatedCounters = CreateGpuCounters("GPU Adapter Memory", "Dedicated Usage", PreferredMemoryCounterInstance);
        _gpuSharedCounters = CreateGpuCounters("GPU Adapter Memory", "Shared Usage", PreferredMemoryCounterInstance);
    }

    private static IReadOnlyList<PerformanceCounter> CreateGpuCounters(string categoryName, string counterName, Func<string, bool> include)
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(categoryName))
            {
                return [];
            }

            PerformanceCounterCategory category = new(categoryName);
            string[] instances = category.GetInstanceNames()
                .Where(instance => !string.IsNullOrWhiteSpace(instance) && include(instance))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (instances.Any(instance => instance.Equals("_Total", StringComparison.OrdinalIgnoreCase)))
            {
                instances = ["_Total"];
            }

            List<PerformanceCounter> counters = [];
            foreach (string instance in instances)
            {
                try
                {
                    PerformanceCounter counter = new(categoryName, counterName, instance, readOnly: true);
                    _ = counter.NextValue();
                    counters.Add(counter);
                }
                catch
                {
                }
            }

            return counters;
        }
        catch
        {
            return [];
        }
    }

    private static bool PreferredMemoryCounterInstance(string instance) =>
        instance.Equals("_Total", StringComparison.OrdinalIgnoreCase)
        || !instance.Contains("pid_", StringComparison.OrdinalIgnoreCase);

    private static double ReadCounterValue(PerformanceCounter counter)
    {
        try
        {
            return Math.Max(0, counter.NextValue());
        }
        catch
        {
            return 0;
        }
    }

    private static long SumCounterBytes(IReadOnlyList<PerformanceCounter>? counters)
    {
        if (counters is null)
        {
            return 0;
        }

        double total = 0;
        foreach (PerformanceCounter counter in counters)
        {
            total += ReadCounterValue(counter);
        }

        return (long)Math.Min(long.MaxValue, Math.Max(0, total));
    }

    private static bool IsPhysicalNic(NetworkInterface adapter, HashSet<string> physicalAdapterIds)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        if (!physicalAdapterIds.Contains(NormalizeAdapterId(adapter.Id)))
        {
            return false;
        }

        bool supportedType = adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.Wireless80211;
        if (!supportedType)
        {
            return false;
        }

        string text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
        string[] virtualHints =
        [
            "virtual",
            "vpn",
            "tap",
            "tunnel",
            "wintun",
            "wireguard",
            "tailscale",
            "zerotier",
            "hyper-v",
            "vmware",
            "virtualbox",
            "docker",
            "bluetooth"
        ];

        return !virtualHints.Any(text.Contains);
    }

    private static HashSet<string> ReadPhysicalAdapterIds()
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (root is null)
        {
            return ids;
        }

        foreach (string subKeyName in root.GetSubKeyNames())
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(subKeyName);
                string? id = Convert.ToString(key?.GetValue("NetCfgInstanceId"));
                string? componentId = Convert.ToString(key?.GetValue("ComponentId"));
                int physicalMedia = Convert.ToInt32(key?.GetValue("*PhysicalMediaType") ?? 0);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(componentId) || physicalMedia == 0)
                {
                    continue;
                }

                string component = componentId.ToLowerInvariant();
                if (component.StartsWith("ms_", StringComparison.Ordinal)
                    || component.StartsWith("vms_", StringComparison.Ordinal)
                    || component.StartsWith("root\\", StringComparison.Ordinal)
                    || component.StartsWith("sw\\", StringComparison.Ordinal))
                {
                    continue;
                }

                ids.Add(NormalizeAdapterId(id));
            }
            catch
            {
                continue;
            }
        }

        return ids;
    }

    private static string NormalizeAdapterId(string id) => id.Trim().Trim('{', '}');

    private static string IpAddress(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.Address.ToString())
                .FirstOrDefault() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private double CurrentCpuSpeedGhz()
    {
        uint currentMhz = NativeMethods.QueryProcessorCurrentMhz();
        return currentMhz == 0 ? _cpuInfo.BaseSpeedGhz : currentMhz / 1000d;
    }

    private static CpuInfo ReadCpuInfo()
    {
        SystemLogicalProcessorInformation[] topology = NativeMethods.QueryLogicalProcessorInformation();
        int sockets = topology.Count(info => info.Relationship == LogicalProcessorRelationship.ProcessorPackage);
        int cores = topology.Count(info => info.Relationship == LogicalProcessorRelationship.ProcessorCore);
        ulong l1 = SumCache(topology, 1);
        ulong l2 = SumCache(topology, 2);
        ulong l3 = SumCache(topology, 3);
        uint maxMhz = NativeMethods.QueryProcessorMaxMhz();
        if (maxMhz == 0)
        {
            maxMhz = QueryRegistryMhz();
        }

        return new CpuInfo(
            QueryCpuName(),
            maxMhz / 1000d,
            Math.Max(1, sockets),
            Math.Max(1, cores),
            Environment.ProcessorCount,
            NativeMethods.IsVirtualizationFirmwareEnabled(),
            l1,
            l2,
            l3);
    }

    private static ulong SumCache(SystemLogicalProcessorInformation[] topology, byte level) =>
        (ulong)topology
            .Where(info => info.Relationship == LogicalProcessorRelationship.Cache && info.ProcessorInformation.Cache.Level == level)
            .Sum(info => (long)info.ProcessorInformation.Cache.Size);

    private static string QueryCpuName()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        string? name = Convert.ToString(key?.GetValue("ProcessorNameString"))?.Trim();
        return string.IsNullOrWhiteSpace(name) ? "CPU" : name;
    }

    private static uint QueryRegistryMhz()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        object? value = key?.GetValue("~MHz");
        return value is int mhz && mhz > 0 ? (uint)mhz : 0;
    }

    public void Dispose()
    {
        foreach (PerformanceCounter counter in _diskActiveCounters.Values)
        {
            counter.Dispose();
        }

        foreach (PerformanceCounter counter in _diskReadCounters.Values)
        {
            counter.Dispose();
        }

        foreach (PerformanceCounter counter in _diskWriteCounters.Values)
        {
            counter.Dispose();
        }

        DisposeCounters(_gpuUtilCounters);
        DisposeCounters(_gpuDedicatedCounters);
        DisposeCounters(_gpuSharedCounters);
    }

    private static void DisposeCounters(IReadOnlyList<PerformanceCounter>? counters)
    {
        if (counters is null)
        {
            return;
        }

        foreach (PerformanceCounter counter in counters)
        {
            counter.Dispose();
        }
    }
}
