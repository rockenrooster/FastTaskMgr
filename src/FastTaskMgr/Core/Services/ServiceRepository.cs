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

        // A service registered between the size query and the read makes the buffer too small;
        // retry with the new size instead of showing an empty list.
        const int errorMoreData = 234;
        int bytesNeeded = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int resume = 0;
            IntPtr buffer = bytesNeeded > 0 ? Marshal.AllocHGlobal(bytesNeeded) : IntPtr.Zero;
            try
            {
                if (NativeMethods.EnumServicesStatusEx(
                    manager,
                    0,
                    NativeMethods.ServiceWin32,
                    NativeMethods.ServiceStateAll,
                    buffer,
                    bytesNeeded,
                    out int moreBytesNeeded,
                    out int returned,
                    ref resume,
                    null))
                {
                    return ReadServices(manager, buffer, returned);
                }

                if (Marshal.GetLastWin32Error() != errorMoreData || moreBytesNeeded <= 0)
                {
                    return [];
                }

                bytesNeeded += moreBytesNeeded;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        return [];
    }

    private ServiceRow[] ReadServices(SafeServiceHandle manager, IntPtr buffer, int returned)
    {
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

        // One handle serves both queries (each open is an RPC to the service control manager).
        using SafeServiceHandle service = NativeMethods.OpenService(manager, serviceName, NativeMethods.ServiceQueryConfig);
        (string Description, string Group) config = service.IsInvalid
            ? ("", "")
            : (QueryDescription(service), QueryGroup(service));
        _configCache[serviceName] = config;
        return config;
    }

    private static string QueryGroup(SafeServiceHandle service)
    {
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

    private static string QueryDescription(SafeServiceHandle service)
    {
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
