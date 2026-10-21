using System.Runtime.InteropServices;
using System.Text;

namespace FastTaskMgr.App;

/// <summary>
/// Lets a second launch bring the running instance forward. A registered message is used
/// because the running window may be hidden in the tray (so it is no process's visible
/// main window) or elevated (so UIPI drops lower-integrity messages unless it opts in).
/// </summary>
internal static class SingleInstance
{
    private const uint MsgFltAllow = 1;

    public static readonly int ActivateMessage = RegisterWindowMessage("FastTaskMgr.ActivateInstance.6E1C2F3A");

    /// <summary>Called by the running instance once its main window handle exists.</summary>
    public static void AllowActivationFrom(IntPtr hwnd)
    {
        if (ActivateMessage != 0)
        {
            _ = ChangeWindowMessageFilterEx(hwnd, (uint)ActivateMessage, MsgFltAllow, IntPtr.Zero);
        }
    }

    /// <summary>Called by a second launch: asks the running main window to restore itself.</summary>
    public static void ActivateExisting()
    {
        if (ActivateMessage == 0)
        {
            return;
        }

        uint self = (uint)Environment.ProcessId;
        StringBuilder text = new(64);
        _ = EnumWindows((hwnd, lParam) =>
        {
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == self)
            {
                return true;
            }

            text.Clear();
            if (GetWindowText(hwnd, text, text.Capacity) <= 0 || text.ToString() != MainForm.WindowTitle)
            {
                return true;
            }

            text.Clear();
            if (GetClassName(hwnd, text, text.Capacity) > 0 && text.ToString().StartsWith("WindowsForms", StringComparison.Ordinal))
            {
                // This launch holds the foreground right (the user just started it); pass it on.
                _ = AllowSetForegroundWindow(pid);
                _ = PostMessage(hwnd, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
            }

            return true;
        }, IntPtr.Zero);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilterStruct);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
