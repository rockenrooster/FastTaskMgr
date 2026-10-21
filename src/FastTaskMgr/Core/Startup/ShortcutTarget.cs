using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace FastTaskMgr.Core.Startup;

/// <summary>Reads a shortcut's target path through the shell's own .lnk parser.</summary>
internal static class ShortcutTarget
{
    private const int StgmRead = 0;

    public static string? Resolve(string shortcutPath)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, StgmRead);
            StringBuilder target = new(1024);
            ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return target.Length > 0 ? target.ToString() : null;
        }
        catch
        {
            return null; // unreadable or not a file shortcut
        }
        finally
        {
            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    // Only GetPath is called; it is the first method after IUnknown, so the rest can stay undeclared.
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
    }
}
