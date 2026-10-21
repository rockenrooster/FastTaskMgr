using FastTaskMgr.App;
using FastTaskMgr.Diagnostics;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace FastTaskMgr;

internal static class Program
{
    private const string InstanceMutexName = @"Local\FastTaskMgr.SingleInstance";
    private static bool _errorReported;

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
            SingleInstance.ActivateExisting();
            return 0;
        }

        InstallExceptionHandlers();
        ApplicationConfiguration.Initialize();
        using AppState state = AppState.Load();
        if (state.Settings.AlwaysStartAsAdmin && !IsAdministrator())
        {
            // Free the instance slot first: the elevated copy can reach its own check before this
            // process exits, and would then take this process for the running instance and quit.
            instanceMutex.ReleaseMutex();
            if (TryRelaunchAsAdministrator(args))
            {
                return 0;
            }

            // UAC was declined: carry on unelevated if nothing else took the slot meanwhile.
            if (!TryReacquire(instanceMutex))
            {
                SingleInstance.ActivateExisting();
                return 0;
            }
        }

        Application.Run(new MainForm(state));
        return 0;
    }

    private static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUiException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                ErrorLog.Write("Unhandled", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write("Unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    // A UI-thread failure is logged and the app keeps running; the user is told once per session.
    private static void ReportUiException(Exception exception)
    {
        ErrorLog.Write("UI thread", exception);
        if (_errorReported)
        {
            return;
        }

        _errorReported = true;
        MessageBox.Show(
            $"FastTaskMgr hit an unexpected error and will keep running.{Environment.NewLine}{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}Details: {ErrorLog.FilePath}",
            "FastTaskMgr",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static bool TryReacquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
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
}
