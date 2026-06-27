namespace FastTaskMgr.Core.Services;

internal sealed record ServiceRow(
    string Name,
    string DisplayName,
    string Description,
    string Status,
    int ProcessId,
    string Group);
