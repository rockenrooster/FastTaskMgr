namespace FastTaskMgr.Core.Startup;

internal enum StartupItemKind
{
    Registry,
    StartupFolder,
    /// <summary>A packaged (MSIX/Store) app's startup task.</summary>
    Package
}

/// <summary>Whose Run key or startup folder an entry comes from.</summary>
internal enum StartupScope
{
    CurrentUser,
    LocalMachine,
    /// <summary>The 32-bit (WOW6432Node) HKLM Run key.</summary>
    LocalMachine32
}

/// <param name="Name">The program's file description, as Task Manager shows it, or the entry's own name when there is none.</param>
/// <param name="EntryName">The Run value name or startup file name, which is also its value name under Explorer\StartupApproved;
/// for a packaged app, its "{package family}\{task id}" state key.</param>
/// <param name="MovedByFastTaskMgr">Disabled by an older FastTaskMgr, which moved the Run value or renamed the file to *.disabled; enabling puts it back.</param>
internal sealed record StartupItem(
    string Name,
    string Publisher,
    bool Enabled,
    string Command,
    string Source,
    string? FilePath,
    StartupItemKind Kind,
    StartupScope Scope,
    string EntryName,
    bool MovedByFastTaskMgr);
