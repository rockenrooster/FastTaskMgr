using FastTaskMgr.App;
using FastTaskMgr.Diagnostics;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace FastTaskMgr;

internal static class Program
{
    private const string InstanceMutexName = @"Local\FastTaskMgr.SingleInstance";
    private const int SwRestore = 9;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-check", StringComparer.OrdinalIgnoreCase))
        {
            return SelfCheck.Run();
        }

        using Mutex instanceMutex = new(true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            FocusExistingInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using AppState state = AppState.Load();
        if (state.Settings.AlwaysStartAsAdmin && !IsAdministrator())
        {
            if (TryRelaunchAsAdministrator(args))
            {
                return 0;
            }
        }

        Application.Run(new MainForm(state));
        return 0;
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool TryRelaunchAsAdministrator(string[] args)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = Application.ExecutablePath,
                Arguments = string.Join(" ", args.Select(QuoteArgument)),
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string QuoteArgument(string argument) =>
        argument.Contains(' ') || argument.Contains('"')
            ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : argument;

    private static void FocusExistingInstance()
    {
        int currentProcessId = Environment.ProcessId;
        string processName = Process.GetCurrentProcess().ProcessName;
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == currentProcessId || process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                _ = ShowWindow(process.MainWindowHandle, SwRestore);
                _ = SetForegroundWindow(process.MainWindowHandle);
                return;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
