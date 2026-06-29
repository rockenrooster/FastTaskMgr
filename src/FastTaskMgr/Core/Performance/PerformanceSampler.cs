using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using FastTaskMgr.Core.Native;
using Microsoft.Win32;

namespace FastTaskMgr.Core.Performance;

internal sealed class PerformanceSampler : IDisposable
{
    private readonly Lock _lock = new();
    private readonly CpuInfo _cpuInfo = ReadCpuInfo();
    private readonly MemorySpec _memorySpec = ReadMemorySpec();
    private readonly GpuInfo _gpuInfo = ReadGpuInfo();
    private readonly HashSet<string> _physicalAdapterIds = ReadPhysicalAdapterIds();
    private readonly Dictionary<string, PerformanceCounter> _diskActiveCounters = [];
    private readonly Dictionary<string, PerformanceCounter> _diskReadCounters = [];
    private readonly Dictionary<string, PerformanceCounter> _diskWriteCounters = [];
    private readonly Dictionary<string, (long sent, long received, DateTime time)> _lastNetworkBytes = [];
    private IReadOnlyList<PerformanceCounter>? _gpuUtilCounters;
    private IReadOnlyList<PerformanceCounter>? _gpuDedicatedCounters;
    private IReadOnlyList<PerformanceCounter>? _gpuSharedCounters;
    private bool _gpuCountersInitialized;
    private bool _compressedMemoryUnavailable;
    private PerformanceCounter? _compressedMemoryCounter;
    private ProcessorPerformanceInformation[] _lastProcessorInfo = [];
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;

    public PerformanceSample Sample(bool fastFirstPaint = false)
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
                new MemoryDetails(
                    (ulong)info.KernelPaged * pageSize,
                    (ulong)info.KernelNonpaged * pageSize,
                    SampleCompressedMemory(pageSize, fastFirstPaint),
                    _memorySpec.SpeedMtps,
                    _memorySpec.SlotsUsed,
                    _memorySpec.SlotsTotal),
                info.ProcessCount,
                info.ThreadCount,
                info.HandleCount,
                TimeSpan.FromMilliseconds(Environment.TickCount64),
                SampleDisks(fastFirstPaint),
                SampleNetworks(),
                fastFirstPaint ? [] : SampleGpus());
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

    private DiskPerformanceSample[] SampleDisks(bool fastFirstPaint)
    {
        List<DiskPerformanceSample> disks = [];
        int index = 0;
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            string driveName = drive.Name.TrimEnd('\\');
            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;
            long used = total - free;
            disks.Add(new DiskPerformanceSample(
                $"disk:{drive.Name}",
                $"Disk {index} ({driveName})",
                drive.DriveFormat,
                fastFirstPaint ? 0 : Math.Clamp(ReadCounter(_diskActiveCounters, "% Disk Time", driveName), 0, 100),
                fastFirstPaint ? 0 : ReadCounter(_diskReadCounters, "Disk Read Bytes/sec", driveName),
                fastFirstPaint ? 0 : ReadCounter(_diskWriteCounters, "Disk Write Bytes/sec", driveName),
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
                counter = new PerformanceCounter("LogicalDisk", counterName, instance, readOnly: true);
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
        double utilization = 0;
        if (_gpuUtilCounters is not null)
        {
            foreach (PerformanceCounter counter in _gpuUtilCounters)
            {
                utilization += ReadCounterValue(counter);
            }
        }

        if ((_gpuUtilCounters is null || _gpuUtilCounters.Count == 0) && _gpuInfo == GpuInfo.Unknown)
        {
            return [];
        }

        long dedicated = SumCounterBytes(_gpuDedicatedCounters);
        long shared = SumCounterBytes(_gpuSharedCounters);
        return
        [
            new GpuPerformanceSample(
                "gpu:0",
                _gpuInfo.Name,
                _gpuUtilCounters is { Count: > 0 } ? "Windows GPU counters" : "Adapter info only",
                Math.Clamp(utilization, 0, 100),
                dedicated,
                _gpuInfo.DedicatedMemoryTotalBytes,
                shared,
                null)
        ];
    }

    private ulong? SampleCompressedMemory(ulong pageSize, bool fastFirstPaint)
    {
        if (fastFirstPaint || _compressedMemoryUnavailable)
        {
            return null;
        }

        try
        {
            _compressedMemoryCounter ??= new PerformanceCounter("Memory", "Compressed Page Count", readOnly: true);
            return (ulong)Math.Max(0, _compressedMemoryCounter.NextValue()) * pageSize;
        }
        catch
        {
            _compressedMemoryCounter?.Dispose();
            _compressedMemoryCounter = null;
            _compressedMemoryUnavailable = true;
            return null;
        }
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

    private static GpuInfo ReadGpuInfo()
    {
        GpuInfo best = GpuInfo.Unknown;
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (root is null)
            {
                return best;
            }

            foreach (string subKeyName in root.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? key = root.OpenSubKey($@"{subKeyName}\0000");
                    string name = RegistryValueString(key?.GetValue("HardwareInformation.AdapterString"));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = RegistryValueString(key?.GetValue("DriverDesc"));
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    long total = Math.Max(
                        RegistryValueInt64(key?.GetValue("HardwareInformation.qwMemorySize")),
                        RegistryValueInt64(key?.GetValue("HardwareInformation.MemorySize")));
                    GpuInfo candidate = new(name, total);
                    if (best == GpuInfo.Unknown || candidate.DedicatedMemoryTotalBytes > best.DedicatedMemoryTotalBytes)
                    {
                        best = candidate;
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch
        {
        }

        return best;
    }

    private static string RegistryValueString(object? value)
    {
        string text = value switch
        {
            byte[] bytes => Encoding.Unicode.GetString(bytes),
            string stringValue => stringValue,
            _ => Convert.ToString(value) ?? ""
        };
        return text.Trim('\0', ' ', '\t', '\r', '\n');
    }

    private static long RegistryValueInt64(object? value)
    {
        switch (value)
        {
            case int number:
                return number >= 0 ? number : unchecked((uint)number);
            case uint number:
                return number;
            case long number:
                return Math.Max(0, number);
            case ulong number:
                return number > long.MaxValue ? long.MaxValue : (long)number;
            case byte[] bytes when bytes.Length >= 8:
                long signed = BitConverter.ToInt64(bytes, 0);
                return signed >= 0 ? signed : (long)Math.Min(BitConverter.ToUInt64(bytes, 0), (ulong)long.MaxValue);
            case byte[] bytes when bytes.Length >= 4:
                return BitConverter.ToUInt32(bytes, 0);
        }

        return long.TryParse(Convert.ToString(value), out long parsed) ? Math.Max(0, parsed) : 0;
    }

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

    private static MemorySpec ReadMemorySpec()
    {
        byte[] raw = NativeMethods.QueryRawSmbiosData();
        if (raw.Length < 8)
        {
            return new MemorySpec(0, 0, 0);
        }

        int tableLength = (int)Math.Min(BitConverter.ToUInt32(raw, 4), raw.Length - 8);
        int offset = 8;
        int end = offset + tableLength;
        int slots = 0;
        int used = 0;
        int speed = 0;

        while (offset + 4 <= end)
        {
            byte type = raw[offset];
            int length = raw[offset + 1];
            if (length < 4 || offset + length > end)
            {
                break;
            }

            if (type == 17)
            {
                slots++;
                if (MemoryDeviceSizeMb(raw, offset, length) > 0)
                {
                    used++;
                    speed = Math.Max(speed, MemoryDeviceSpeedMtps(raw, offset, length));
                }
            }
            else if (type == 127)
            {
                break;
            }

            int next = offset + length;
            while (next + 1 < end && (raw[next] != 0 || raw[next + 1] != 0))
            {
                next++;
            }

            offset = next + 2;
        }

        return new MemorySpec(speed, used, slots);
    }

    private static ulong MemoryDeviceSizeMb(byte[] raw, int offset, int length)
    {
        ushort size = ReadUInt16(raw, offset, length, 0x0C);
        if (size == 0 || size == 0xFFFF)
        {
            return 0;
        }

        if (size == 0x7FFF)
        {
            return ReadUInt32(raw, offset, length, 0x1C);
        }

        ulong value = (ulong)(size & 0x7FFF);
        return (size & 0x8000) == 0 ? value : value / 1024;
    }

    private static int MemoryDeviceSpeedMtps(byte[] raw, int offset, int length)
    {
        ushort configured = ReadUInt16(raw, offset, length, 0x20);
        ushort speed = ReadUInt16(raw, offset, length, 0x15);
        return configured > 0 ? configured : speed;
    }

    private static ushort ReadUInt16(byte[] raw, int offset, int length, int fieldOffset) =>
        fieldOffset + 2 <= length ? BitConverter.ToUInt16(raw, offset + fieldOffset) : (ushort)0;

    private static uint ReadUInt32(byte[] raw, int offset, int length, int fieldOffset) =>
        fieldOffset + 4 <= length ? BitConverter.ToUInt32(raw, offset + fieldOffset) : 0;

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
        _compressedMemoryCounter?.Dispose();
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

    private sealed record MemorySpec(int SpeedMtps, int SlotsUsed, int SlotsTotal);

    private sealed record GpuInfo(string Name, long DedicatedMemoryTotalBytes)
    {
        public static readonly GpuInfo Unknown = new("GPU 0", 0);
    }
}
