using System.Text.Json;

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
    public string DefaultPage { get; set; } = "Processes";
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
    public int WindowLeft { get; set; } = -1;
    public int WindowTop { get; set; } = -1;

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

            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        JsonSerializerOptions options = new() { WriteIndented = true };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, options));
    }
}

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
