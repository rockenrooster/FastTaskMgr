using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FastTaskMgr.Core.Native;

internal sealed class SafeNativeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeNativeHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeServiceHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
}
