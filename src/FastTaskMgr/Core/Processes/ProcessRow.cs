namespace FastTaskMgr.Core.Processes;

/// <param name="MemoryBytes">Task Manager's Memory column: the "active" private working set,
/// which is the private working set, or 0 while the process is suspended.</param>
internal sealed record ProcessRow(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string Status,
    double CpuPercent,
    long MemoryBytes,
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
