using FastTaskMgr.App;
using FastTaskMgr.Diagnostics;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace FastTaskMgr;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-check", StringComparer.OrdinalIgnoreCase))
        {
            return SelfCheck.Run();
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
}
