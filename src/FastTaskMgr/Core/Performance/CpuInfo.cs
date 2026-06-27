namespace FastTaskMgr.Core.Performance;

internal sealed record CpuInfo(
    string Name,
    double BaseSpeedGhz,
    int Sockets,
    int Cores,
    int LogicalProcessors,
    bool VirtualizationEnabled,
    ulong L1CacheBytes,
    ulong L2CacheBytes,
    ulong L3CacheBytes);
