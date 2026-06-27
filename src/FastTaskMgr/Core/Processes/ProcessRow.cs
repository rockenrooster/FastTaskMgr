namespace FastTaskMgr.Core.Processes;

internal sealed record ProcessRow(
    int ProcessId,
    int? ParentProcessId,
    string Name,
    string Status,
    double CpuPercent,
    long WorkingSetBytes,
    int ThreadCount,
    int HandleCount,
    string? Path,
    string? UserName,
    string Architecture,
    string Description,
    string Priority,
    string Affinity);
