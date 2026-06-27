namespace FastTaskMgr.Core.Performance;

internal sealed record PerformanceSample(
    double CpuPercent,
    IReadOnlyList<double> CpuCorePercents,
    CpuInfo CpuInfo,
    double CurrentCpuSpeedGhz,
    ulong TotalMemoryBytes,
    ulong AvailableMemoryBytes,
    ulong CommitTotalBytes,
    ulong CommitLimitBytes,
    ulong SystemCacheBytes,
    int ProcessCount,
    int ThreadCount,
    int HandleCount,
    TimeSpan Uptime,
    IReadOnlyList<DiskPerformanceSample> Disks,
    IReadOnlyList<NetworkPerformanceSample> Networks,
    IReadOnlyList<GpuPerformanceSample> Gpus);

internal sealed record DiskPerformanceSample(
    string Key,
    string Name,
    string Kind,
    double ActivePercent,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    long UsedBytes,
    long FreeBytes,
    long TotalBytes);

internal sealed record NetworkPerformanceSample(
    string Key,
    string Name,
    string Description,
    string IpAddress,
    long LinkSpeedBitsPerSecond,
    double SendBytesPerSecond,
    double ReceiveBytesPerSecond,
    double UtilizationPercent);

internal sealed record GpuPerformanceSample(
    string Key,
    string Name,
    string Description,
    double UtilizationPercent,
    long DedicatedMemoryBytes,
    long SharedMemoryBytes);
