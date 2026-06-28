using Microsoft.Win32;

namespace FastTaskMgr.UI.Theme;

internal static class AppTheme
{
    public static void Apply(Control root, string theme)
    {
        bool dark = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            || (theme.Equals("System", StringComparison.OrdinalIgnoreCase) && SystemPrefersDark());

        Color back = dark ? Color.FromArgb(32, 32, 32) : Color.White;
        Color panel = dark ? Color.FromArgb(42, 42, 42) : Color.FromArgb(247, 247, 247);
        Color fore = dark ? Color.WhiteSmoke : Color.FromArgb(24, 24, 24);
        ApplyRecursive(root, back, panel, fore);
    }

    private static void ApplyRecursive(Control control, Color back, Color panel, Color fore)
    {
        control.ForeColor = fore;
        control.BackColor = control is Button or TextBox or ComboBox or ListView ? panel : back;

        foreach (Control child in control.Controls)
        {
            ApplyRecursive(child, back, panel, fore);
        }
    }

    private static bool SystemPrefersDark()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        object? value = key?.GetValue("AppsUseLightTheme");
        return value is int intValue && intValue == 0;
    }
}
