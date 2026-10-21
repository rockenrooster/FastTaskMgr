using FastTaskMgr.App;

namespace FastTaskMgr.Diagnostics;

/// <summary>
/// Best-effort error log in LocalAppData. Never throws; repeats of the same failure are
/// collapsed so a refresh that fails every tick cannot flood the disk.
/// </summary>
internal static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();
    private static string _lastEntryKey = "";
    private static long _lastEntryAt;

    public static string FilePath => Path.Combine(AppSettings.DirectoryPath, "error.log");

    public static void Write(string context, Exception exception)
    {
        try
        {
            string key = $"{context}|{exception.GetType().FullName}|{exception.Message}";
            lock (Gate)
            {
                long now = Environment.TickCount64;
                if (key == _lastEntryKey && now - _lastEntryAt < 60_000)
                {
                    return;
                }

                _lastEntryKey = key;
                _lastEntryAt = now;
                Directory.CreateDirectory(AppSettings.DirectoryPath);
                FileInfo file = new(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{context}] {exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
