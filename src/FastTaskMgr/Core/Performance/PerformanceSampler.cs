using System.Runtime.InteropServices;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using FastTaskMgr.Core.Native;
using Microsoft.Win32;

namespace FastTaskMgr.Core.Performance;

internal sealed class PerformanceSampler : IDisposable
{
    // Volume layout, capacity and the adapter list change rarely but are the most expensive
    // parts of a sample (several file-system calls per drive letter, and a full adapter
    // enumeration that costs milliseconds on machines with many virtual adapters). They refresh
    // on these intervals (adapters also on network change events); activity stays per tick.
    private const long DiskInventoryRefreshMs = 10_000;
    private const long NetworkAdapterRefreshMs = 15_000;
    private static readonly string[] HiddenAdapterHints =
    [
        "vpn",
        "tap",
        "tunnel",
        "wintun",
        "wireguard",
        "tailscale",
        "zerotier",
        "docker",
        "bluetooth",
        "loopback"
    ];

    private readonly Lock _lock = new();
    private readonly CpuInfo _cpuInfo = ReadCpuInfo();
    private readonly MemorySpec _memorySpec = ReadMemorySpec();
    private readonly GpuInfo _gpuInfo = ReadGpuInfo();
    private readonly GpuEngineTotals _gpuEngines = new();
    private readonly Dictionary<int, string> _diskModels = [];
    private Dictionary<int, DiskInventory> _diskInventory = [];
    private long _diskInventoryRefreshedAt = long.MinValue;
    private Dictionary<int, string> _poolMemberDiskToLetter = [];
    private long _poolMappingRefreshedAt = long.MinValue;
    private readonly Dictionary<string, (long sent, long received, DateTime time)> _lastNetworkBytes = [];
    private NetworkAdapter[] _networkAdapters = [];
    private long _networkAdaptersRefreshedAt = long.MinValue;
    private volatile bool _networkAdaptersChanged;
    private readonly NetworkAddressChangedEventHandler _onNetworkAddressChanged;
    private readonly NetworkAvailabilityChangedEventHandler _onNetworkAvailabilityChanged;
    private bool _networkChangeSubscribed;
    private bool _disposed;
    private PdhQuery? _pdh;
    private bool _pdhInitialized;
    private IntPtr _diskIdleCounter;
    private IntPtr _diskReadCounter;
    private IntPtr _diskWriteCounter;
    private IntPtr _gpuUtilCounter;
    private IntPtr _gpuDedicatedCounter;
    private IntPtr _gpuSharedCounter;
    private IntPtr _compressedPagesCounter;
    private IntPtr _cpuPerformanceCounter;
    private ProcessorPerformanceInformation[] _lastProcessorInfo = [];
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;

    public PerformanceSampler()
    {
        _onNetworkAddressChanged = (_, _) => _networkAdaptersChanged = true;
        _onNetworkAvailabilityChanged = (_, _) => _networkAdaptersChanged = true;
    }

    public PerformanceSample Sample(bool fastFirstPaint = false)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool collected = CollectPdh(fastFirstPaint);
            double cpuPercent = SampleCpu();
            double[] corePercents = SampleCpuCores();
            MemoryStatusEx memory = MemoryStatusEx.Create();
            _ = NativeMethods.GlobalMemoryStatusEx(ref memory);
            _ = NativeMethods.GetPerformanceInfo(out PerformanceInformation info, Marshal.SizeOf<PerformanceInformation>());
            ulong pageSize = info.PageSize;

            PerformanceSample sample = new(
                cpuPercent,
                corePercents,
                _cpuInfo,
                CurrentCpuSpeedGhz(collected),
                memory.ullTotalPhys,
                memory.ullAvailPhys,
                (ulong)info.CommitTotal * pageSize,
                (ulong)info.CommitLimit * pageSize,
                (ulong)info.SystemCache * pageSize,
                new MemoryDetails(
                    (ulong)info.KernelPaged * pageSize,
                    (ulong)info.KernelNonpaged * pageSize,
                    SampleCompressedMemory(pageSize, collected),
                    _memorySpec.SpeedMtps,
                    _memorySpec.SlotsUsed,
                    _memorySpec.SlotsTotal),
                info.ProcessCount,
                info.ThreadCount,
                info.HandleCount,
                TimeSpan.FromMilliseconds(Environment.TickCount64),
                SampleDisks(collected),
                SampleNetworks(),
                collected ? SampleGpus() : []);
            return sample;
        }
    }

    // The first PDH touch costs over a second; keep it off the first-paint path. A background
    // warm-up sample at app start normally pays this before the Performance page ever opens.
    private bool CollectPdh(bool fastFirstPaint)
    {
        if (!_pdhInitialized)
        {
            if (fastFirstPaint)
            {
                return false;
            }

            _pdhInitialized = true;
            _pdh = new PdhQuery();
            _diskIdleCounter = _pdh.AddCounter(@"\PhysicalDisk(*)\% Idle Time");
            _diskReadCounter = _pdh.AddCounter(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
            _diskWriteCounter = _pdh.AddCounter(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
            _gpuUtilCounter = _pdh.AddCounter(@"\GPU Engine(*)\Utilization Percentage");
            _gpuDedicatedCounter = _pdh.AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _gpuSharedCounter = _pdh.AddCounter(@"\GPU Adapter Memory(*)\Shared Usage");
            _compressedPagesCounter = _pdh.AddCounter(@"\Memory\Compressed Page Count");
            _cpuPerformanceCounter = _pdh.AddCounter(@"\Processor Information(_Total)\% Processor Performance");
        }

        return _pdh is not null && _pdh.Collect();
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

    // Physical disks, like Windows Task Manager. Logical-volume counters miss virtual
    // volumes whose driver bypasses the volume perf stack (e.g. StableBit DrivePool),
    // where activity is only visible on the underlying member disks. Disk identity comes
    // from the volume table so tiles render before PDH data exists.
    private DiskPerformanceSample[] SampleDisks(bool collected)
    {
        Dictionary<int, double> idleByDisk = ReadDiskValues(collected, _diskIdleCounter);
        Dictionary<int, double> readByDisk = ReadDiskValues(collected, _diskReadCounter);
        Dictionary<int, double> writeByDisk = ReadDiskValues(collected, _diskWriteCounter);
        RefreshDiskInventoryIfDue();

        List<RawDisk> raw = [];
        foreach (int index in _diskInventory.Keys.Union(idleByDisk.Keys).OrderBy(index => index))
        {
            DiskInventory inventory = _diskInventory.GetValueOrDefault(index) ?? DiskInventory.Empty;
            if (!_diskModels.TryGetValue(index, out string? model))
            {
                model = NativeMethods.QueryDiskModel(index);
                _diskModels[index] = model;
            }

            double idle = idleByDisk.TryGetValue(index, out double idleValue) ? idleValue : 100;
            raw.Add(new RawDisk(
                index,
                inventory.Letters,
                model,
                inventory.Formats,
                Math.Clamp(100 - idle, 0, 100),
                readByDisk.GetValueOrDefault(index),
                writeByDisk.GetValueOrDefault(index),
                inventory.TotalBytes,
                inventory.FreeBytes));
        }

        RefreshPoolMappingIfDue(raw);
        Dictionary<int, string> memberPools = _poolMemberDiskToLetter;

        List<DiskPerformanceSample> tiles = [];
        foreach (RawDisk disk in raw.OrderBy(disk => disk.Letters.FirstOrDefault() ?? "￿").ThenBy(disk => disk.Index))
        {
            if (memberPools.ContainsKey(disk.Index))
            {
                continue; // shown nested under its pool tile
            }

            RawDisk[] members = disk.Letters.Length == 0 || !IsPoolDisk(disk.Model)
                ? []
                : raw
                    .Where(candidate => memberPools.TryGetValue(candidate.Index, out string? pool)
                        && disk.Letters.Contains(pool, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(candidate => candidate.Index)
                    .ToArray();
            if (members.Length == 0)
            {
                tiles.Add(StandaloneSample(disk));
                continue;
            }

            tiles.Add(new DiskPerformanceSample(
                $"disk:{disk.Index}",
                $"Pool ({string.Join(" ", disk.Letters)})",
                Kind(disk),
                members.Max(member => member.ActivePercent),
                members.Sum(member => member.ReadBytesPerSecond),
                members.Sum(member => member.WriteBytesPerSecond),
                disk.TotalBytes - disk.FreeBytes,
                disk.FreeBytes,
                disk.TotalBytes,
                disk.Index,
                members.Select(StandaloneSample).ToArray()));
        }

        return tiles.ToArray();
    }

    // Letters, file systems and capacity per physical disk, from the volume table.
    private void RefreshDiskInventoryIfDue()
    {
        long now = Environment.TickCount64;
        if (_diskInventoryRefreshedAt != long.MinValue && now - _diskInventoryRefreshedAt < DiskInventoryRefreshMs)
        {
            return;
        }

        _diskInventoryRefreshedAt = now;
        Dictionary<int, List<string>> lettersByDisk = [];
        foreach (VolumeInfo volume in NativeMethods.QueryVolumes())
        {
            string? letter = volume.MountPaths.FirstOrDefault(path => path.Length == 3 && path[1] == ':');
            foreach (int disk in volume.DiskNumbers)
            {
                if (!lettersByDisk.TryGetValue(disk, out List<string>? letters))
                {
                    letters = [];
                    lettersByDisk[disk] = letters;
                }

                if (letter is not null)
                {
                    letters.Add(letter[..2]);
                }
            }
        }

        Dictionary<int, DiskInventory> inventory = [];
        foreach ((int index, List<string> found) in lettersByDisk)
        {
            string[] letters = found.Order().ToArray();
            long total = 0;
            long free = 0;
            List<string> formats = [];
            foreach (string letter in letters)
            {
                try
                {
                    DriveInfo info = new(letter);
                    if (info.IsReady)
                    {
                        total += info.TotalSize;
                        free += info.AvailableFreeSpace;
                        formats.Add(info.DriveFormat);
                    }
                }
                catch
                {
                }
            }

            inventory[index] = new DiskInventory(letters, formats.Distinct().ToArray(), total, free);
        }

        _diskInventory = inventory;
    }

    private static DiskPerformanceSample StandaloneSample(RawDisk disk)
    {
        string label = disk.Letters.Length > 0 ? string.Join(" ", disk.Letters) : disk.Model;
        return new DiskPerformanceSample(
            $"disk:{disk.Index}",
            label.Length > 0 ? $"Disk {disk.Index} ({label})" : $"Disk {disk.Index}",
            Kind(disk),
            disk.ActivePercent,
            disk.ReadBytesPerSecond,
            disk.WriteBytesPerSecond,
            disk.TotalBytes - disk.FreeBytes,
            disk.FreeBytes,
            disk.TotalBytes,
            disk.Index);
    }

    private static string Kind(RawDisk disk)
    {
        string kind = disk.Model;
        if (disk.Formats.Length > 0)
        {
            string fileSystems = string.Join(", ", disk.Formats);
            kind = kind.Length > 0 ? $"{kind} ({fileSystems})" : fileSystems;
        }

        return kind;
    }

    private static bool IsPoolDisk(string model) =>
        model.Contains("covecube", StringComparison.OrdinalIgnoreCase)
        || model.Contains("covefs", StringComparison.OrdinalIgnoreCase);

    // Pool member volumes are hidden (no drive letter) and carry a "PoolPart.*" folder at
    // their root. Attribution to a specific pool compares top-level folder names inside the
    // PoolPart with each pool root; requires elevation to read, degrades to flat tiles.
    private void RefreshPoolMappingIfDue(IReadOnlyList<RawDisk> raw)
    {
        if (_poolMappingRefreshedAt != long.MinValue && Environment.TickCount64 - _poolMappingRefreshedAt < 60000)
        {
            return;
        }

        _poolMappingRefreshedAt = Environment.TickCount64;
        try
        {
            _poolMemberDiskToLetter = BuildPoolMemberMap(raw
                .Where(disk => disk.Letters.Length > 0 && IsPoolDisk(disk.Model))
                .SelectMany(disk => disk.Letters)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }
        catch
        {
            _poolMemberDiskToLetter = [];
        }
    }

    private static Dictionary<int, string> BuildPoolMemberMap(IReadOnlyList<string> poolLetters)
    {
        Dictionary<int, string> map = [];
        if (poolLetters.Count == 0)
        {
            return map;
        }

        Dictionary<string, HashSet<string>> poolRootDirs = poolLetters.ToDictionary(
            letter => letter,
            letter => TopLevelDirectoryNames(letter + "\\"),
            StringComparer.OrdinalIgnoreCase);

        foreach (VolumeInfo volume in NativeMethods.QueryVolumes())
        {
            if (volume.MountPaths.Any(path => path.Length <= 3) || volume.DiskNumbers.Length == 0)
            {
                continue; // volumes with a drive letter are not hidden pool members
            }

            string? poolPart;
            try
            {
                poolPart = Directory.EnumerateDirectories(volume.VolumeGuidPath, "PoolPart.*").FirstOrDefault();
            }
            catch
            {
                continue;
            }

            if (poolPart is null)
            {
                continue;
            }

            string? pool = AttributePool(poolLetters, poolRootDirs, TopLevelDirectoryNames(poolPart));
            if (pool is null)
            {
                continue;
            }

            foreach (int disk in volume.DiskNumbers)
            {
                map[disk] = pool;
            }
        }

        return map;
    }

    private static string? AttributePool(
        IReadOnlyList<string> poolLetters,
        IReadOnlyDictionary<string, HashSet<string>> poolRootDirs,
        HashSet<string> memberDirs)
    {
        if (poolLetters.Count == 1)
        {
            return poolLetters[0];
        }

        string? best = null;
        int bestScore = 0;
        bool tie = false;
        foreach ((string letter, HashSet<string> rootDirs) in poolRootDirs)
        {
            int score = memberDirs.Count(rootDirs.Contains);
            if (score > bestScore)
            {
                best = letter;
                bestScore = score;
                tie = false;
            }
            else if (score == bestScore && score > 0)
            {
                tie = true;
            }
        }

        return tie ? null : best;
    }

    private static HashSet<string> TopLevelDirectoryNames(string root)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string path in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(path);
                if (name.Length > 0 && name[0] != '.' && name[0] != '$'
                    && !name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
        }

        return names;
    }

    private sealed record DiskInventory(string[] Letters, string[] Formats, long TotalBytes, long FreeBytes)
    {
        public static readonly DiskInventory Empty = new([], [], 0, 0);
    }

    private sealed record NetworkAdapter(NetworkInterface Interface, string Key, string IpAddress);

    private sealed record RawDisk(
        int Index,
        string[] Letters,
        string Model,
        string[] Formats,
        double ActivePercent,
        double ReadBytesPerSecond,
        double WriteBytesPerSecond,
        long TotalBytes,
        long FreeBytes);

    // PDH wildcard instances look like "5 D:"; the leading number is the physical disk index.
    private Dictionary<int, double> ReadDiskValues(bool collected, IntPtr counter)
    {
        Dictionary<int, double> values = [];
        if (!collected || _pdh is null)
        {
            return values;
        }

        _pdh.VisitArray(counter, (instance, value) =>
        {
            int cut = instance.IndexOf(' ');
            if (int.TryParse(cut < 0 ? instance : instance[..cut], out int index))
            {
                values[index] = Math.Max(0, value);
            }
        });

        return values;
    }

    private void RefreshNetworkAdaptersIfDue()
    {
        // Subscribed on first use (always a background sample): the first subscription
        // initializes Winsock, which should not land on the UI thread via the constructor.
        if (!_networkChangeSubscribed)
        {
            _networkChangeSubscribed = true;
            try
            {
                NetworkChange.NetworkAddressChanged += _onNetworkAddressChanged;
                NetworkChange.NetworkAvailabilityChanged += _onNetworkAvailabilityChanged;
            }
            catch
            {
                // Without change notifications the adapter list still refreshes on its interval.
            }
        }

        long now = Environment.TickCount64;
        if (!_networkAdaptersChanged
            && _networkAdaptersRefreshedAt != long.MinValue
            && now - _networkAdaptersRefreshedAt < NetworkAdapterRefreshMs)
        {
            return;
        }

        _networkAdaptersChanged = false;
        _networkAdaptersRefreshedAt = now;
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return; // keep the previous list
        }

        List<NetworkAdapter> visible = [];
        foreach (NetworkInterface adapter in all)
        {
            string ipAddress = IpAddress(adapter);
            if (IsVisibleNetworkAdapter(adapter, ipAddress))
            {
                visible.Add(new NetworkAdapter(adapter, $"net:{adapter.Id}", ipAddress));
            }
        }

        _networkAdapters = visible.ToArray();
        foreach (string key in _lastNetworkBytes.Keys.Where(key => !visible.Any(adapter => adapter.Key == key)).ToArray())
        {
            _lastNetworkBytes.Remove(key);
        }
    }

    private NetworkPerformanceSample[] SampleNetworks()
    {
        RefreshNetworkAdaptersIfDue();
        DateTime now = DateTime.UtcNow;
        List<NetworkPerformanceSample> networks = new(_networkAdapters.Length);
        foreach ((NetworkInterface adapter, string key, string ipAddress) in _networkAdapters)
        {
            // Byte counters are re-read per tick (GetIfEntry2); only the adapter list is cached.
            IPv4InterfaceStatistics stats;
            try
            {
                stats = adapter.GetIPv4Statistics();
            }
            catch
            {
                _networkAdaptersChanged = true; // adapter removed; re-enumerate on the next tick
                continue;
            }

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
                ipAddress,
                adapter.Speed,
                send,
                receive,
                utilization));
        }

        return networks.ToArray();
    }

    private GpuPerformanceSample[] SampleGpus()
    {
        _gpuEngines.Clear();
        _pdh?.VisitArray(_gpuUtilCounter, _gpuEngines.Add);
        if (_gpuEngines.InstanceCount == 0 && _gpuInfo == GpuInfo.Unknown)
        {
            return [];
        }

        return
        [
            new GpuPerformanceSample(
                "gpu:0",
                _gpuInfo.Name,
                _gpuEngines.InstanceCount > 0 ? "Windows GPU counters" : "Adapter info only",
                Math.Clamp(_gpuEngines.Busiest, 0, 100),
                ReadGpuMemoryBytes(_gpuDedicatedCounter),
                _gpuInfo.DedicatedMemoryTotalBytes,
                ReadGpuMemoryBytes(_gpuSharedCounter))
        ];
    }

    private long ReadGpuMemoryBytes(IntPtr counter)
    {
        double total = 0;
        bool hasTotal = false;
        _pdh?.VisitArray(counter, (instance, value) =>
        {
            if (hasTotal)
            {
                return;
            }

            if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            {
                total = value;
                hasTotal = true;
            }
            else if (!instance.Contains("pid_", StringComparison.OrdinalIgnoreCase))
            {
                total += value;
            }
        });

        return (long)Math.Min(long.MaxValue, Math.Max(0, total));
    }

    private ulong? SampleCompressedMemory(ulong pageSize, bool collected)
    {
        if (!collected || _pdh is null)
        {
            return null;
        }

        double? pages = _pdh.ReadValue(_compressedPagesCounter);
        return pages is null ? null : (ulong)Math.Max(0, pages.Value) * pageSize;
    }

    internal static double BusiestGpuEnginePercent(IEnumerable<(string InstanceName, double Percent)> samples)
    {
        GpuEngineTotals totals = new();
        foreach ((string instanceName, double percent) in samples)
        {
            totals.Add(instanceName, percent);
        }

        return totals.Busiest;
    }

    /// <summary>
    /// Sums utilization per engine type ("3D", "Copy", ...) across processes and reports the
    /// busiest type. Works on spans so hundreds of engine instances per tick allocate nothing.
    /// </summary>
    private sealed class GpuEngineTotals
    {
        private readonly Dictionary<string, double> _byType = new(StringComparer.OrdinalIgnoreCase);

        public int InstanceCount { get; private set; }

        public double Busiest
        {
            get
            {
                double busiest = 0;
                foreach (double value in _byType.Values)
                {
                    busiest = Math.Max(busiest, value);
                }

                return busiest;
            }
        }

        public void Clear()
        {
            _byType.Clear();
            InstanceCount = 0;
        }

        public void Add(ReadOnlySpan<char> instance, double percent)
        {
            InstanceCount++;
            if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            const string marker = "engtype_";
            int start = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            ReadOnlySpan<char> engineType = start < 0 ? instance : instance[(start + marker.Length)..];
            Dictionary<string, double>.AlternateLookup<ReadOnlySpan<char>> lookup = _byType.GetAlternateLookup<ReadOnlySpan<char>>();
            lookup[engineType] = (lookup.TryGetValue(engineType, out double current) ? current : 0) + Math.Max(0, percent);
        }
    }

    private static bool IsVisibleNetworkAdapter(NetworkInterface adapter, string ipAddress)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ipAddress))
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
        return !HiddenAdapterHints.Any(text.Contains);
    }

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

        // REG_BINARY adapter strings are NUL padded and can have an odd length, which decodes
        // to a stray replacement character after the padding.
        int terminator = text.IndexOf('\0');
        return (terminator < 0 ? text : text[..terminator]).Trim();
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
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                .Select(address => address.Address.ToString())
                .FirstOrDefault() ?? "";
        }
        catch
        {
            return "";
        }
    }

    // % Processor Performance is the real clock relative to base speed, boost included, and is
    // what Task Manager shows. CurrentMhz from the power API stops at base speed on most CPUs,
    // so it only covers samples taken before PDH has two collections to compare.
    private double CurrentCpuSpeedGhz(bool collected)
    {
        if (collected && _pdh?.ReadValue(_cpuPerformanceCounter) is > 0 and double percent)
        {
            return _cpuInfo.BaseSpeedGhz * percent / 100;
        }

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
            NativeMethods.LogicalProcessorCount,
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

    // Waits for an in-flight background sample before closing the PDH query under it.
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pdh?.Dispose();
            _pdh = null;
            if (_networkChangeSubscribed)
            {
                try
                {
                    NetworkChange.NetworkAddressChanged -= _onNetworkAddressChanged;
                    NetworkChange.NetworkAvailabilityChanged -= _onNetworkAvailabilityChanged;
                }
                catch
                {
                }
            }
        }
    }

    private sealed record MemorySpec(int SpeedMtps, int SlotsUsed, int SlotsTotal);

    private sealed record GpuInfo(string Name, long DedicatedMemoryTotalBytes)
    {
        public static readonly GpuInfo Unknown = new("GPU 0", 0);
    }
}
