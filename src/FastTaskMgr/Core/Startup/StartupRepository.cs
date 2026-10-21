using System.Buffers.Binary;
using System.Collections.Concurrent;
using FastTaskMgr.Core.Native;
using Microsoft.Win32;

namespace FastTaskMgr.Core.Startup;

/// <summary>
/// Lists the Run keys, startup folders and packaged app startup tasks that Windows Task Manager
/// shows. Run and startup folder state lives in Explorer\StartupApproved, the same values Task
/// Manager and Settings read and write.
/// </summary>
internal sealed class StartupRepository
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Wow64RunKey = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    // Older FastTaskMgr versions disabled entries by moving Run values here or renaming files.
    private const string LegacyDisabledRoot = @"Software\FastTaskMgr\DisabledStartup";
    private const string LegacyDisabledSuffix = ".disabled";

    // Names and publishers come from version resources read off disk; keep them between refreshes.
    private readonly ConcurrentDictionary<string, (string Description, string Company)> _versionStringsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly PackagedStartupTasks _packages = new();

    public IReadOnlyList<StartupItem> ListStartupItems()
    {
        List<StartupItem> items = [];
        AddRunItems(items, StartupScope.CurrentUser);
        AddRunItems(items, StartupScope.LocalMachine);
        if (Environment.Is64BitOperatingSystem)
        {
            AddRunItems(items, StartupScope.LocalMachine32);
        }

        AddLegacyRunItems(items, StartupScope.CurrentUser);
        AddLegacyRunItems(items, StartupScope.LocalMachine);
        AddStartupFolderItems(items, StartupScope.CurrentUser);
        AddStartupFolderItems(items, StartupScope.LocalMachine);
        _packages.AddItems(items);
        return items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void SetEnabled(StartupItem item, bool enabled)
    {
        if (item.Kind == StartupItemKind.Package)
        {
            PackagedStartupTasks.SetEnabled(item, enabled);
            return;
        }

        if (!item.MovedByFastTaskMgr)
        {
            WriteApprovedState(item, enabled);
            return;
        }

        if (!enabled)
        {
            return; // already disabled
        }

        if (item.Kind == StartupItemKind.StartupFolder)
        {
            RestoreRenamedFile(item);
        }
        else
        {
            RestoreMovedValue(item);
        }

        // Windows may still hold its own "disabled" flag from before the move.
        WriteApprovedState(item, enabled: true);
    }

    private void AddRunItems(List<StartupItem> items, StartupScope scope)
    {
        using RegistryKey root = OpenRunRoot(scope);
        using RegistryKey? run = root.OpenSubKey(RunKey);
        if (run is null)
        {
            return;
        }

        using RegistryKey? approved = OpenApprovedKey(StartupItemKind.Registry, scope);
        string source = scope switch
        {
            StartupScope.CurrentUser => @"HKCU\" + RunKey,
            StartupScope.LocalMachine => @"HKLM\" + RunKey,
            _ => @"HKLM\" + Wow64RunKey
        };
        foreach (string name in run.GetValueNames())
        {
            AddRegistryItem(items, run, name, IsApproved(approved, name), source, scope, movedByFastTaskMgr: false);
        }
    }

    private void AddLegacyRunItems(List<StartupItem> items, StartupScope scope)
    {
        string subKey = LegacyRunKey(scope);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey);
        if (key is null)
        {
            return;
        }

        foreach (string name in key.GetValueNames())
        {
            AddRegistryItem(items, key, name, enabled: false, @"HKCU\" + subKey, scope, movedByFastTaskMgr: true);
        }
    }

    private void AddRegistryItem(
        List<StartupItem> items,
        RegistryKey key,
        string name,
        bool enabled,
        string source,
        StartupScope scope,
        bool movedByFastTaskMgr)
    {
        string command = Convert.ToString(key.GetValue(name)) ?? "";
        string? path = ProgramPath(TryExtractExecutablePath(command));
        (string displayName, string publisher) = Describe(path, name);
        items.Add(new StartupItem(
            displayName,
            publisher,
            enabled,
            command,
            source,
            path,
            StartupItemKind.Registry,
            scope,
            name,
            movedByFastTaskMgr));
    }

    private void AddStartupFolderItems(List<StartupItem> items, StartupScope scope)
    {
        bool currentUser = scope == StartupScope.CurrentUser;
        string folder = Environment.GetFolderPath(currentUser ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        using RegistryKey? approved = OpenApprovedKey(StartupItemKind.StartupFolder, scope);
        foreach (string path in Directory.EnumerateFiles(folder))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue; // folder display settings, never launched
            }

            bool moved = fileName.EndsWith(LegacyDisabledSuffix, StringComparison.OrdinalIgnoreCase);
            string entryName = moved ? fileName[..^LegacyDisabledSuffix.Length] : fileName;
            string ownName = Path.GetFileNameWithoutExtension(entryName);
            // A shortcut is named after its target, as in Task Manager.
            (string displayName, string publisher) = moved
                ? (ownName, Describe(path, ownName).Publisher)
                : Describe(entryName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget.Resolve(path) : path, ownName);
            items.Add(new StartupItem(
                displayName,
                publisher,
                !moved && IsApproved(approved, entryName),
                path,
                currentUser ? "Startup folder" : "Common startup folder",
                path,
                StartupItemKind.StartupFolder,
                scope,
                entryName,
                moved));
        }
    }

    // HKLM is opened per view: the 64-bit and 32-bit (WOW6432Node) Run keys are separate lists.
    private static RegistryKey OpenRunRoot(StartupScope scope) => scope switch
    {
        StartupScope.CurrentUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        StartupScope.LocalMachine => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        _ => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
    };

    private static RegistryKey OpenApprovedRoot(StartupScope scope) => RegistryKey.OpenBaseKey(
        scope == StartupScope.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
        RegistryView.Registry64);

    // StartupApproved\Run32 holds the state of the WOW6432Node Run key.
    private static string ApprovedSubKey(StartupItemKind kind, StartupScope scope) => ApprovedRoot + (kind switch
    {
        StartupItemKind.StartupFolder => @"\StartupFolder",
        _ => scope == StartupScope.LocalMachine32 ? @"\Run32" : @"\Run"
    });

    private static RegistryKey? OpenApprovedKey(StartupItemKind kind, StartupScope scope)
    {
        using RegistryKey root = OpenApprovedRoot(scope);
        return root.OpenSubKey(ApprovedSubKey(kind, scope));
    }

    // Each value is a flags DWORD whose low bit means disabled, then the FILETIME it was disabled.
    // No value means enabled.
    private static bool IsApproved(RegistryKey? approved, string name) =>
        approved?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;

    private static void WriteApprovedState(StartupItem item, bool enabled)
    {
        byte[]? current;
        using (RegistryKey? approved = OpenApprovedKey(item.Kind, item.Scope))
        {
            if (IsApproved(approved, item.EntryName) == enabled)
            {
                return;
            }

            current = approved?.GetValue(item.EntryName) as byte[];
        }

        // Task Manager writes 2 (enabled) or 3 (disabled); keep any other flag bits already set.
        uint flags = current is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(current) & ~1u : 0;
        if (flags == 0)
        {
            flags = 2;
        }

        byte[] data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, enabled ? flags : flags | 1);
        if (!enabled)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
        }

        using RegistryKey root = OpenApprovedRoot(item.Scope);
        using RegistryKey key = root.CreateSubKey(ApprovedSubKey(item.Kind, item.Scope), writable: true);
        key.SetValue(item.EntryName, data, RegistryValueKind.Binary);
    }

    private static void RestoreMovedValue(StartupItem item)
    {
        using RegistryKey? legacy = Registry.CurrentUser.OpenSubKey(LegacyRunKey(item.Scope), writable: true);
        object? value = legacy?.GetValue(item.EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (legacy is null || value is null)
        {
            return;
        }

        using RegistryKey root = OpenRunRoot(item.Scope);
        using RegistryKey run = root.CreateSubKey(RunKey, writable: true);
        // The app may have registered itself again since; keep its current command then.
        if (run.GetValue(item.EntryName) is null)
        {
            run.SetValue(item.EntryName, value, legacy.GetValueKind(item.EntryName));
        }

        legacy.DeleteValue(item.EntryName, throwOnMissingValue: false);
    }

    private static void RestoreRenamedFile(StartupItem item)
    {
        string? folder = Path.GetDirectoryName(item.FilePath);
        if (folder is null || !File.Exists(item.FilePath))
        {
            return;
        }

        string target = Path.Combine(folder, item.EntryName);
        if (File.Exists(target))
        {
            throw new IOException($"{item.EntryName} already exists in the startup folder.");
        }

        File.Move(item.FilePath, target);
    }

    private static string LegacyRunKey(StartupScope scope) =>
        LegacyDisabledRoot + (scope == StartupScope.CurrentUser ? @"\HKCU_Run" : @"\HKLM_Run");

    private static string? TryExtractExecutablePath(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        int exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? trimmed[..(exe + 4)] : trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    // A bare program name ("rundll32.exe ...") is looked up where Windows finds it.
    private static string? ProgramPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        string[] folders = [Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows)];
        foreach (string folder in folders)
        {
            string candidate = Path.Combine(folder, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    // Task Manager names an entry after its program: the file description, else the file name.
    // Entries whose program is missing keep their own name.
    private (string Name, string Publisher) Describe(string? programPath, string ownName)
    {
        if (string.IsNullOrWhiteSpace(programPath) || !File.Exists(programPath))
        {
            return (ownName, ""); // not cached: the program may be installed later
        }

        if (!_versionStringsByPath.TryGetValue(programPath, out (string Description, string Company) strings))
        {
            strings = VersionStrings.Read(programPath);
            _versionStringsByPath[programPath] = strings;
        }

        return (strings.Description.Length > 0 ? strings.Description : Path.GetFileName(programPath), strings.Company);
    }
}
