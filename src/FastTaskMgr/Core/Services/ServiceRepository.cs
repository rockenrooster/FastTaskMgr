using System.ComponentModel;
using System.Runtime.InteropServices;
using FastTaskMgr.Core.Native;

namespace FastTaskMgr.Core.Services;

internal sealed class ServiceRepository
{
    public IReadOnlyList<ServiceRow> ListServices()
    {
        using SafeServiceHandle manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerEnumerateService);
        if (manager.IsInvalid)
        {
            return [];
        }

        int resume = 0;
        _ = NativeMethods.EnumServicesStatusEx(
            manager,
            0,
            NativeMethods.ServiceWin32,
            NativeMethods.ServiceStateAll,
            IntPtr.Zero,
            0,
            out int bytesNeeded,
            out _,
            ref resume,
            null);

        if (bytesNeeded <= 0)
        {
            return [];
        }

        IntPtr buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            resume = 0;
            if (!NativeMethods.EnumServicesStatusEx(
                manager,
                0,
                NativeMethods.ServiceWin32,
                NativeMethods.ServiceStateAll,
                buffer,
                bytesNeeded,
                out _,
                out int returned,
                ref resume,
                null))
            {
                return [];
            }

            int size = Marshal.SizeOf<EnumServiceStatusProcess>();
            List<ServiceRow> rows = new(returned);
            for (int index = 0; index < returned; index++)
            {
                IntPtr itemPtr = IntPtr.Add(buffer, index * size);
                EnumServiceStatusProcess item = Marshal.PtrToStructure<EnumServiceStatusProcess>(itemPtr);
                string name = Marshal.PtrToStringUni(item.lpServiceName) ?? "";
                rows.Add(new ServiceRow(
                    name,
                    Marshal.PtrToStringUni(item.lpDisplayName) ?? name,
                    QueryDescription(manager, name),
                    StateName(item.ServiceStatusProcess.dwCurrentState),
                    checked((int)item.ServiceStatusProcess.dwProcessId),
                    ""));
            }

            return rows.OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Start(string serviceName)
    {
        using SafeServiceHandle manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        using SafeServiceHandle service = NativeMethods.OpenService(manager, serviceName, NativeMethods.ServiceStart);
        if (manager.IsInvalid || service.IsInvalid || !NativeMethods.StartService(service, 0, null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Stop(string serviceName)
    {
        using SafeServiceHandle manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        using SafeServiceHandle service = NativeMethods.OpenService(manager, serviceName, NativeMethods.ServiceStop | NativeMethods.ServiceQueryStatus);
        if (manager.IsInvalid || service.IsInvalid || !NativeMethods.ControlService(service, NativeMethods.ServiceControlStop, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static string QueryDescription(SafeServiceHandle manager, string serviceName)
    {
        using SafeServiceHandle service = NativeMethods.OpenService(manager, serviceName, NativeMethods.ServiceQueryConfig);
        if (service.IsInvalid)
        {
            return "";
        }

        _ = NativeMethods.QueryServiceConfig2(service, NativeMethods.ServiceConfigDescription, IntPtr.Zero, 0, out int bytesNeeded);
        if (bytesNeeded <= 0)
        {
            return "";
        }

        IntPtr buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!NativeMethods.QueryServiceConfig2(service, NativeMethods.ServiceConfigDescription, buffer, bytesNeeded, out _))
            {
                return "";
            }

            ServiceDescription description = Marshal.PtrToStructure<ServiceDescription>(buffer);
            return Marshal.PtrToStringUni(description.lpDescription) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string StateName(uint state) => state switch
    {
        1 => "Stopped",
        2 => "Start pending",
        3 => "Stop pending",
        4 => "Running",
        5 => "Continue pending",
        6 => "Pause pending",
        7 => "Paused",
        _ => "Unknown"
    };
}
