using System.Diagnostics;
using Microsoft.Win32;

namespace FastTaskMgr.Core.Startup;

internal sealed class StartupRepository
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DisabledRoot = @"Software\FastTaskMgr\DisabledStartup";

    public IReadOnlyList<StartupItem> ListStartupItems()
    {
        List<StartupItem> items = [];
        AddRegistryItems(items, Registry.CurrentUser, "HKCU", RunKey, true);
        AddRegistryItems(items, Registry.LocalMachine, "HKLM", RunKey, true);
        AddRegistryItems(items, Registry.CurrentUser, "HKCU", DisabledRoot + @"\HKCU_Run", false);
        AddRegistryItems(items, Registry.CurrentUser, "HKLM", DisabledRoot + @"\HKLM_Run", false);
        AddStartupFolderItems(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder");
        AddStartupFolderItems(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Common startup folder");
        return items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void SetEnabled(StartupItem item, bool enabled)
    {
        if (item.Kind == StartupItemKind.StartupFolder)
        {
            ToggleStartupFile(item, enabled);
            return;
        }

        ToggleRegistryItem(item, enabled);
    }

    private static void AddRegistryItems(List<StartupItem> items, RegistryKey hive, string hiveName, string subKey, bool enabled)
    {
        using RegistryKey? key = hive.OpenSubKey(subKey);
        if (key is null)
        {
            return;
        }

        foreach (string name in key.GetValueNames())
        {
            string command = Convert.ToString(key.GetValue(name)) ?? "";
            string? path = TryExtractExecutablePath(command);
            items.Add(new StartupItem(
                name,
                GetPublisher(path),
                enabled,
                command,
                enabled ? $"{hiveName}\\{RunKey}" : $"{hiveName}\\{subKey}",
                path,
                StartupItemKind.Registry,
                hiveName,
                enabled ? RunKey : subKey));
        }
    }

    private static void AddStartupFolderItems(List<StartupItem> items, string folder, string source)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(folder))
        {
            bool enabled = !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            string realPath = enabled ? path : path[..^".disabled".Length];
            items.Add(new StartupItem(
                Path.GetFileNameWithoutExtension(realPath),
                GetPublisher(realPath),
                enabled,
                path,
                source,
                path,
                StartupItemKind.StartupFolder,
                null,
                null));
        }
    }

    private static void ToggleRegistryItem(StartupItem item, bool enabled)
    {
        string sourceSubKey = item.RegistrySubKey ?? RunKey;
        string targetSubKey = item.Enabled
            ? DisabledRoot + (item.RegistryHive == "HKLM" ? @"\HKLM_Run" : @"\HKCU_Run")
            : RunKey;

        RegistryKey sourceHive = item.Enabled && item.RegistryHive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        RegistryKey targetHive = enabled && item.RegistryHive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;

        using RegistryKey? source = sourceHive.OpenSubKey(sourceSubKey, writable: true);
        using RegistryKey target = targetHive.CreateSubKey(targetSubKey, writable: true);
        object? value = source?.GetValue(item.Name);
        if (source is null || value is null)
        {
            return;
        }

        target.SetValue(item.Name, value, source.GetValueKind(item.Name));
        source.DeleteValue(item.Name, false);
    }

    private static void ToggleStartupFile(StartupItem item, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath))
        {
            return;
        }

        string from = item.FilePath;
        string to = enabled && from.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? from[..^".disabled".Length]
            : from + ".disabled";

        if (File.Exists(from) && !File.Exists(to))
        {
            File.Move(from, to);
        }
    }

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

    private static string GetPublisher(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
                ? ""
                : FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
        }
        catch
        {
            return "";
        }
    }
}
