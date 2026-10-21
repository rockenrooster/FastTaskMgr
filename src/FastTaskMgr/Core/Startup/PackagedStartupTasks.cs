using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using FastTaskMgr.Core.Native;
using Microsoft.Win32;

namespace FastTaskMgr.Core.Startup;

/// <summary>
/// Startup tasks of packaged (MSIX/Store) apps. Each task's StartupTaskState lives under the
/// user's AppModel\SystemAppData\{family}\{task id} key, which Task Manager and Settings read and
/// write; names, publishers and programs come from the package manifest.
/// </summary>
internal sealed class PackagedStartupTasks
{
    private const string SystemAppDataKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    // Windows.ApplicationModel.StartupTaskState; 0 is Disabled.
    private const int DisabledByUser = 1;
    private const int Enabled = 2;
    private const int DisabledByPolicy = 3;
    private const int EnabledByPolicy = 4;

    // A manifest never changes for a given full name, which includes the package version.
    private readonly ConcurrentDictionary<string, PackageInfo?> _packagesByFullName = new(StringComparer.OrdinalIgnoreCase);

    private sealed record PackageInfo(string Publisher, IReadOnlyDictionary<string, TaskInfo> Tasks);
    private sealed record TaskInfo(string Name, string? ProgramPath);

    public void AddItems(List<StartupItem> items)
    {
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(SystemAppDataKey);
        if (root is null)
        {
            return;
        }

        foreach (string family in root.GetSubKeyNames())
        {
            using RegistryKey? familyKey = root.OpenSubKey(family);
            if (familyKey is null)
            {
                continue;
            }

            PackageInfo? package = null;
            bool packageLoaded = false;
            foreach (string taskId in familyKey.GetSubKeyNames())
            {
                object? state;
                using (RegistryKey? taskKey = familyKey.OpenSubKey(taskId))
                {
                    state = taskKey?.GetValue("State");
                }

                if (state is not int value)
                {
                    continue; // other per-app data
                }

                if (!packageLoaded)
                {
                    package = LoadPackage(family);
                    packageLoaded = true;
                }

                // Leftover state of an app that is no longer installed, or not a startup task.
                if (package is null || !package.Tasks.TryGetValue(taskId, out TaskInfo? task))
                {
                    continue;
                }

                items.Add(new StartupItem(
                    task.Name,
                    package.Publisher,
                    value is Enabled or EnabledByPolicy,
                    task.ProgramPath ?? "",
                    $"Packaged app ({family})",
                    task.ProgramPath,
                    StartupItemKind.Package,
                    StartupScope.CurrentUser,
                    family + @"\" + taskId,
                    MovedByFastTaskMgr: false));
            }
        }
    }

    // Writes the same values Settings writes when the switch is flipped.
    public static void SetEnabled(StartupItem item, bool enabled)
    {
        using RegistryKey task = Registry.CurrentUser.OpenSubKey(SystemAppDataKey + @"\" + item.EntryName, writable: true)
            ?? throw new InvalidOperationException($"{item.Name} is no longer installed.");
        if (task.GetValue("State") is DisabledByPolicy or EnabledByPolicy)
        {
            throw new InvalidOperationException($"{item.Name} is managed by your organization, so its startup setting can't be changed.");
        }

        if (enabled)
        {
            task.SetValue("State", Enabled, RegistryValueKind.DWord);
            task.SetValue("UserEnabledStartupOnce", 1, RegistryValueKind.DWord);
        }
        else
        {
            task.SetValue("State", DisabledByUser, RegistryValueKind.DWord);
            task.SetValue("LastDisabledTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RegistryValueKind.QWord);
        }
    }

    private PackageInfo? LoadPackage(string family)
    {
        string? fullName = MainPackageFullName(family);
        return fullName is null ? null : _packagesByFullName.GetOrAdd(fullName, ReadManifest);
    }

    private static PackageInfo? ReadManifest(string fullName)
    {
        string? folder = PackagePath(fullName);
        if (folder is null)
        {
            return null;
        }

        XElement? package;
        try
        {
            package = XDocument.Load(Path.Combine(folder, "AppxManifest.xml")).Root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }

        if (package is null)
        {
            return null;
        }

        XNamespace ns = package.Name.Namespace;
        string identity = (string?)package.Element(ns + "Identity")?.Attribute("Name") ?? "";
        XElement? properties = package.Element(ns + "Properties");
        string packageName = Resolve(fullName, identity, (string?)properties?.Element(ns + "DisplayName"));
        string publisher = Resolve(fullName, identity, (string?)properties?.Element(ns + "PublisherDisplayName"));

        Dictionary<string, TaskInfo> tasks = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement startupTask in package.Descendants().Where(element => element.Name.LocalName == "StartupTask"))
        {
            string? taskId = (string?)startupTask.Attribute("TaskId");
            if (string.IsNullOrEmpty(taskId) || tasks.ContainsKey(taskId))
            {
                continue;
            }

            // The extension may name its own program; otherwise the task starts the application's.
            string? program = (string?)startupTask.Parent?.Attribute("Executable")
                ?? (string?)startupTask.Ancestors().FirstOrDefault(element => element.Name.LocalName == "Application")?.Attribute("Executable");
            string taskName = Resolve(fullName, identity, (string?)startupTask.Attribute("DisplayName"));
            tasks[taskId] = new TaskInfo(
                taskName.Length > 0 ? taskName : packageName.Length > 0 ? packageName : taskId,
                program is null ? null : Path.Combine(folder, program.Replace('/', '\\')));
        }

        return new PackageInfo(publisher, tasks);
    }

    // "ms-resource:Key" names a string in the package's Resources map; absolute
    // ms-resource://Name/Map/Key URIs are passed through.
    private static string Resolve(string fullName, string identity, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        const string scheme = "ms-resource:";
        if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return value.Trim();
        }

        string key = value[scheme.Length..];
        string uri = key.StartsWith("//", StringComparison.Ordinal) ? value
            : key.StartsWith('/') ? $"ms-resource://{identity}{key}"
            : key.Contains('/') ? $"ms-resource://{identity}/{key}"
            : $"ms-resource://{identity}/Resources/{key}";
        char[] buffer = new char[512];
        if (SHLoadIndirectString($"@{{{fullName}?{uri}}}", buffer, (uint)buffer.Length, IntPtr.Zero) != 0)
        {
            return "";
        }

        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end >= 0 ? end : buffer.Length).Trim();
    }

    // A family can also hold resource packages; the main package has an empty resource ID
    // (Name_Version_Architecture__PublisherId).
    private static string? MainPackageFullName(string family)
    {
        uint count = 0;
        uint length = 0;
        if (GetPackagesByPackageFamily(family, ref count, null, ref length, null) != NativeMethods.ErrorInsufficientBuffer || count == 0)
        {
            return null; // not installed for this user
        }

        IntPtr[] fullNamePointers = new IntPtr[count];
        char[] buffer = new char[length];
        if (GetPackagesByPackageFamily(family, ref count, fullNamePointers, ref length, buffer) != 0)
        {
            return null;
        }

        string[] fullNames = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return fullNames.FirstOrDefault(name => name.Split('_') is { Length: 5 } parts && parts[3].Length == 0)
            ?? fullNames.FirstOrDefault();
    }

    private static string? PackagePath(string fullName)
    {
        uint length = 0;
        if (GetPackagePathByFullName(fullName, ref length, null) != NativeMethods.ErrorInsufficientBuffer)
        {
            return null;
        }

        char[] buffer = new char[length];
        return GetPackagePathByFullName(fullName, ref length, buffer) == 0
            ? new string(buffer).TrimEnd('\0')
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(
        string packageFamilyName,
        ref uint count,
        [Out] IntPtr[]? packageFullNames,
        ref uint bufferLength,
        [Out] char[]? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, [Out] char[] output, uint outputSize, IntPtr reserved);
}
