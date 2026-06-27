namespace FastTaskMgr.UI;

internal static class FormatUtil
{
    public static string Percent(double value) => $"{value:0.0}%";

    public static string Bytes(ulong value) => Bytes((long)Math.Min(value, long.MaxValue));

    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double current = value;
        int unit = 0;
        while (Math.Abs(current) >= 1024 && unit < units.Length - 1)
        {
            current /= 1024;
            unit++;
        }

        return unit == 0 ? $"{current:0} {units[unit]}" : $"{current:0.0} {units[unit]}";
    }

    public static string Duration(TimeSpan value) => value.TotalDays >= 1
        ? $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m"
        : $"{value.Hours}h {value.Minutes}m";

    public static string BitsPerSecond(long bitsPerSecond)
    {
        string[] units = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
        double current = bitsPerSecond;
        int unit = 0;
        while (Math.Abs(current) >= 1000 && unit < units.Length - 1)
        {
            current /= 1000;
            unit++;
        }

        return unit == 0 ? $"{current:0} {units[unit]}" : $"{current:0.0} {units[unit]}";
    }
}
