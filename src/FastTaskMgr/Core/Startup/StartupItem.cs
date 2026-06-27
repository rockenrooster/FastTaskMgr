namespace FastTaskMgr.Core.Startup;

internal enum StartupItemKind
{
    Registry,
    StartupFolder
}

internal sealed record StartupItem(
    string Name,
    string Publisher,
    bool Enabled,
    string Command,
    string Source,
    string? FilePath,
    StartupItemKind Kind,
    string? RegistryHive,
    string? RegistrySubKey);
