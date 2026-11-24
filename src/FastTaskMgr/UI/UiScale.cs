namespace FastTaskMgr.UI;

/// <summary>
/// Layout sizes in this app are written for 100% display scaling. The process is system-DPI
/// aware (the WinForms default), so fonts already follow the system DPI and one factor, fixed
/// for the life of the process, converts every pixel size to match. On a monitor with another
/// scale Windows stretches the whole window, so nothing changes when it moves.
/// </summary>
internal static class UiScale
{
    public static float Factor { get; private set; } = SystemFactor();

    public static int Px(int value) => (int)MathF.Round(value * Factor);

    public static Size Px(int width, int height) => new(Px(width), Px(height));

    public static Padding Px(Padding value) => new(Px(value.Left), Px(value.Top), Px(value.Right), Px(value.Bottom));

    /// <summary>A font whose size relates to the base font as <paramref name="points"/> relates to the 9 pt default.</summary>
    public static Font RelativeFont(Font baseFont, float points, FontStyle style = FontStyle.Regular) =>
        new(baseFont.FontFamily, baseFont.Size * points / 9f, style);

    /// <summary>
    /// Pixel height of <paramref name="lines"/> lines of <paramref name="font"/> as a label draws them.
    /// Font heights do not scale exactly with the factor, and a label drops a line it cannot show in
    /// full, so text boxes are sized from this rather than from a scaled literal alone.
    /// </summary>
    public static int TextHeight(Font font, int lines = 1) => TextRenderer.MeasureText("Ag", font).Height * lines;

    private static float SystemFactor()
    {
        using Graphics screen = Graphics.FromHwnd(IntPtr.Zero);
        return screen.DpiX / 96f;
    }
}
