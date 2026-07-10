using System.ComponentModel;
using System.Runtime.InteropServices;
using FastTaskMgr.Core.Native;

namespace FastTaskMgr.Core.Services;

internal sealed class ServiceRepository
{
    // Descriptions and load-order groups rarely change; cache them so refreshes
    // stop re-opening every service for config queries.
    private readonly Dictionary<string, (string Description, string Group)> _configCache = new(StringComparer.OrdinalIgnoreCase);

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
                (string description, string group) = ConfigFor(manager, name);
                rows.Add(new ServiceRow(
                    name,
                    Marshal.PtrToStringUni(item.lpDisplayName) ?? name,
                    description,
                    StateName(item.ServiceStatusProcess.dwCurrentState),
                    checked((int)item.ServiceStatusProcess.dwProcessId),
                    group));
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

    public void Restart(string serviceName)
    {
        Stop(serviceName);
        WaitForStopped(serviceName, TimeSpan.FromSeconds(10));
        Start(serviceName);
    }

    private static void WaitForStopped(string serviceName, TimeSpan timeout)
    {
        const uint stopped = 1;
        using SafeServiceHandle manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        using SafeServiceHandle service = NativeMethods.OpenService(manager, serviceName, NativeMethods.ServiceQueryStatus);
        if (manager.IsInvalid || service.IsInvalid)
        {
            return;
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!NativeMethods.QueryServiceStatusEx(service, 0, out ServiceStatusProcess status, Marshal.SizeOf<ServiceStatusProcess>(), out _)
                || status.dwCurrentState == stopped)
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"Service {serviceName} did not stop within {timeout.TotalSeconds:0} seconds.");
    }

    private (string Description, string Group) ConfigFor(SafeServiceHandle manager, string serviceName)
    {
        if (_configCache.TryGetValue(serviceName, out (string Description, string Group) cached))
        {
            return cached;
        }

        (string Description, string Group) config = (QueryDescription(manager, serviceName), QueryGroup(manager, serviceName));
        _configCache[serviceName] = config;
        return config;
    }

    private static string QueryGroup(SafeServiceHandle manager, string serviceName)
    {
        using SafeServiceHandle service = NativeMethods.OpenService(manager, serviceName, NativeMethods.ServiceQueryConfig);
        if (service.IsInvalid)
        {
            return "";
        }

        _ = NativeMethods.QueryServiceConfig(service, IntPtr.Zero, 0, out int bytesNeeded);
        if (bytesNeeded <= 0)
        {
            return "";
        }

        IntPtr buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!NativeMethods.QueryServiceConfig(service, buffer, bytesNeeded, out _))
            {
                return "";
            }

            QueryServiceConfigInfo config = Marshal.PtrToStructure<QueryServiceConfigInfo>(buffer);
            return Marshal.PtrToStringUni(config.lpLoadOrderGroup) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
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
