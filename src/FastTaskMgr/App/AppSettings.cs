using System.Text.Json;
using System.Text.Json.Serialization;
using FastTaskMgr.Diagnostics;

namespace FastTaskMgr.App;

internal enum UpdateSpeed
{
    Paused,
    Low,
    Normal,
    High
}

internal sealed class AppSettings
{
    public const int UnsetPosition = -1;

    public string DefaultPage { get; set; } = "Processes";
    public string? LastPage { get; set; }
    public UpdateSpeed UpdateSpeed { get; set; } = UpdateSpeed.Normal;
    public bool AlwaysOnTop { get; set; }
    public bool MinimizeOnUse { get; set; }
    public bool HideWhenMinimized { get; set; }
    public bool AlwaysStartAsAdmin { get; set; }
    public string Theme { get; set; } = "System";
    public bool ConfirmBeforeEndProcess { get; set; } = true;
    public bool ConfirmBeforeEfficiencyMode { get; set; } = true;
    public int WindowWidth { get; set; } = 1180;
    public int WindowHeight { get; set; } = 760;
    public int WindowLeft { get; set; } = UnsetPosition;
    public int WindowTop { get; set; } = UnsetPosition;
    public bool WindowMaximized { get; set; }
    public Dictionary<string, TableSortState> TableSorts { get; set; } = [];
    public List<string> HiddenPerformanceItems { get; set; } = [];

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastTaskMgr");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            AppSettings? settings = JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJsonContext.Default.AppSettings);
            return settings?.Sanitize() ?? new AppSettings();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Settings load", ex);
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes to a temporary file and swaps it in, so a crash or power loss mid-write
    /// cannot leave a truncated settings.json behind (which would reset every setting).
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string tempPath = FilePath + ".tmp";
        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this, AppSettingsJsonContext.Default.AppSettings);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, FilePath, overwrite: true);
    }

    public bool TrySave()
    {
        try
        {
            Save();
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Settings save", ex);
            return false;
        }
    }

    // Hand-edited or older files can carry values the UI cannot represent.
    private AppSettings Sanitize()
    {
        TableSorts ??= [];
        HiddenPerformanceItems ??= [];
        DefaultPage ??= "Processes";
        Theme = Theme is "System" or "Light" or "Dark" ? Theme : "System";
        if (!Enum.IsDefined(UpdateSpeed))
        {
            UpdateSpeed = UpdateSpeed.Normal;
        }

        WindowWidth = Math.Clamp(WindowWidth, 200, 16384);
        WindowHeight = Math.Clamp(WindowHeight, 200, 16384);
        return this;
    }
}

internal sealed class TableSortState
{
    public string Column { get; set; } = "";
    public bool Descending { get; set; }
}

// Source-generated metadata: no reflection warm-up on every startup and save.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

internal static class UpdateSpeedExtensions
{
    public static int ToMilliseconds(this UpdateSpeed speed) => speed switch
    {
        UpdateSpeed.Paused => 0,
        UpdateSpeed.Low => 5000,
        UpdateSpeed.High => 500,
        _ => 1000
    };
}
