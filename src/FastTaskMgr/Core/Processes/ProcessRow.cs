namespace FastTaskMgr.Core.Processes;

internal sealed record ProcessRow(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string Status,
    double CpuPercent,
    long WorkingSetBytes,
    double DiskBytesPerSecond,
    int ThreadCount,
    int HandleCount,
    int SessionId,
    string? Path,
    string? UserName,
    string Architecture,
    string Description,
    string Company,
    string CommandLine,
    string Priority,
    string Affinity,
    long CreateTime);
