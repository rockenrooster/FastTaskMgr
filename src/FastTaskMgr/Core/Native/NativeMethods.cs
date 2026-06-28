using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FastTaskMgr.Core.Native;

internal static partial class NativeMethods
{
    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessSetInformation = 0x0200;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x0008;
    public const int ErrorInsufficientBuffer = 122;

    public const uint PriorityIdle = 0x00000040;
    public const uint PriorityBelowNormal = 0x00004000;
    public const uint PriorityNormal = 0x00000020;
    public const uint PriorityAboveNormal = 0x00008000;
    public const uint PriorityHigh = 0x00000080;
    public const uint PriorityRealtime = 0x00000100;

    public const uint ScManagerConnect = 0x0001;
    public const uint ScManagerEnumerateService = 0x0004;
    public const uint ServiceQueryConfig = 0x0001;
    public const uint ServiceQueryStatus = 0x0004;
    public const uint ServiceStart = 0x0010;
    public const uint ServiceStop = 0x0020;
    public const uint ServiceWin32 = 0x00000030;
    public const uint ServiceStateAll = 0x00000003;
    public const uint ServiceConfigDescription = 1;
    public const uint ServiceControlStop = 0x00000001;

    public const ushort ImageFileMachineUnknown = 0;
    public const ushort ImageFileMachineI386 = 0x014c;
    public const ushort ImageFileMachineAmd64 = 0x8664;
    public const ushort ImageFileMachineArm64 = 0xaa64;
    private const int ProcessorInformation = 11;
    private const int PfVirtFirmwareEnabled = 21;
    private const int SystemProcessorPerformanceInformation = 8;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeNativeHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeNativeHandle hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeNativeHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(SafeNativeHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(SafeNativeHandle hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(SafeNativeHandle hProcess, out UIntPtr lpProcessAffinityMask, out UIntPtr lpSystemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(SafeNativeHandle hProcess, out ushort pProcessMachine, out ushort pNativeMachine);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeNativeHandle processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeNativeHandle processHandle, uint desiredAccess, out SafeNativeHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeNativeHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupAccountSid(
        string? lpSystemName,
        IntPtr sid,
        StringBuilder name,
        ref int cchName,
        StringBuilder referencedDomainName,
        ref int cchReferencedDomainName,
        out SidNameUse peUse);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref int returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool IsProcessorFeaturePresent(int processorFeature);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        int inputBufferLength,
        IntPtr outputBuffer,
        int outputBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetPerformanceInfo(out PerformanceInformation performanceInformation, int size);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeServiceHandle OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool EnumServicesStatusEx(
        SafeServiceHandle hSCManager,
        int infoLevel,
        uint dwServiceType,
        uint dwServiceState,
        IntPtr lpServices,
        int cbBufSize,
        out int pcbBytesNeeded,
        out int lpServicesReturned,
        ref int lpResumeHandle,
        string? pszGroupName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeServiceHandle OpenService(SafeServiceHandle hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryServiceConfig2(
        SafeServiceHandle hService,
        uint dwInfoLevel,
        IntPtr lpBuffer,
        int cbBufSize,
        out int pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool QueryServiceStatusEx(
        SafeServiceHandle hService,
        int infoLevel,
        out ServiceStatusProcess lpBuffer,
        int cbBufSize,
        out int pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool StartService(SafeServiceHandle hService, int dwNumServiceArgs, string[]? lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool ControlService(SafeServiceHandle hService, uint dwControl, out ServiceStatus serviceStatus);

    public static string? QueryProcessImagePath(int pid)
    {
        using SafeNativeHandle process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
        {
            return null;
        }

        StringBuilder path = new(1024);
        int length = path.Capacity;
        if (QueryFullProcessImageName(process, 0, path, ref length))
        {
            return path.ToString();
        }

        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            return null;
        }

        path = new StringBuilder(32768);
        length = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
    }

    public static int? QueryParentProcessId(int pid)
    {
        using SafeNativeHandle process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
        {
            return null;
        }

        int status = NtQueryInformationProcess(process, 0, out ProcessBasicInformation info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        return status >= 0 ? info.InheritedFromUniqueProcessId.ToInt32() : null;
    }

    public static bool TerminateProcessById(int pid)
    {
        using SafeNativeHandle process = OpenProcess(ProcessTerminate, false, pid);
        return !process.IsInvalid && TerminateProcess(process, 1);
    }

    public static string? QueryProcessUserName(int pid)
    {
        using SafeNativeHandle process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out SafeNativeHandle token))
        {
            return null;
        }

        using (token)
        {
            _ = GetTokenInformation(token, TokenInformationClass.TokenUser, IntPtr.Zero, 0, out int tokenLength);
            if (tokenLength <= 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal(tokenLength);
            try
            {
                if (!GetTokenInformation(token, TokenInformationClass.TokenUser, buffer, tokenLength, out _))
                {
                    return null;
                }

                TokenUser user = Marshal.PtrToStructure<TokenUser>(buffer);
                StringBuilder name = new(256);
                StringBuilder domain = new(256);
                int nameLength = name.Capacity;
                int domainLength = domain.Capacity;

                if (!LookupAccountSid(null, user.User.Sid, name, ref nameLength, domain, ref domainLength, out _))
                {
                    return null;
                }

                return domainLength > 0 ? $"{domain}\\{name}" : name.ToString();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public static string QueryProcessArchitecture(int pid)
    {
        using SafeNativeHandle process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid || !IsWow64Process2(process, out ushort processMachine, out ushort nativeMachine))
        {
            return Environment.Is64BitOperatingSystem ? "x64" : "x86";
        }

        ushort machine = processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine;
        return machine switch
        {
            ImageFileMachineI386 => "x86",
            ImageFileMachineAmd64 => "x64",
            ImageFileMachineArm64 => "ARM64",
            _ => "Unknown"
        };
    }

    public static string QueryPriority(int pid)
    {
        using SafeNativeHandle process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
        {
            return "Access denied";
        }

        return GetPriorityClass(process) switch
        {
            PriorityIdle => "Idle",
            PriorityBelowNormal => "Below normal",
            PriorityNormal => "Normal",
            PriorityAboveNormal => "Above normal",
            PriorityHigh => "High",
            PriorityRealtime => "Realtime",
            _ => "Unknown"
        };
    }

    public static void SetPriority(int pid, uint priority)
    {
        using SafeNativeHandle process = OpenProcess(ProcessSetInformation, false, pid);
        if (process.IsInvalid || !SetPriorityClass(process, priority))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static string QueryAffinity(int pid)
    {
        using SafeNativeHandle process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid || !GetProcessAffinityMask(process, out UIntPtr processMask, out _))
        {
            return "";
        }

        return $"0x{processMask.ToUInt64():X}";
    }

    public static ProcessorPerformanceInformation[] QueryProcessorPerformance()
    {
        int size = Marshal.SizeOf<ProcessorPerformanceInformation>();
        int length = size * Environment.ProcessorCount;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            int status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, length, out int returned);
            if (status < 0 || returned < size)
            {
                return [];
            }

            int count = returned / size;
            ProcessorPerformanceInformation[] result = new ProcessorPerformanceInformation[count];
            for (int index = 0; index < count; index++)
            {
                result[index] = Marshal.PtrToStructure<ProcessorPerformanceInformation>(IntPtr.Add(buffer, index * size));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static SystemLogicalProcessorInformation[] QueryLogicalProcessorInformation()
    {
        int length = 0;
        _ = GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length <= 0)
        {
            return [];
        }

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformation(buffer, ref length))
            {
                return [];
            }

            int size = Marshal.SizeOf<SystemLogicalProcessorInformation>();
            int count = length / size;
            SystemLogicalProcessorInformation[] result = new SystemLogicalProcessorInformation[count];
            for (int index = 0; index < count; index++)
            {
                result[index] = Marshal.PtrToStructure<SystemLogicalProcessorInformation>(IntPtr.Add(buffer, index * size));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static uint QueryProcessorMaxMhz()
    {
        int size = Marshal.SizeOf<ProcessorPowerInformation>() * Math.Max(1, Environment.ProcessorCount);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint status = CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, size);
            if (status != 0)
            {
                return 0;
            }

            ProcessorPowerInformation info = Marshal.PtrToStructure<ProcessorPowerInformation>(buffer);
            return info.MaxMhz;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static uint QueryProcessorCurrentMhz()
    {
        int count = Math.Max(1, Environment.ProcessorCount);
        int itemSize = Marshal.SizeOf<ProcessorPowerInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(itemSize * count);
        try
        {
            uint status = CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, itemSize * count);
            if (status != 0)
            {
                return 0;
            }

            ulong total = 0;
            uint samples = 0;
            for (int index = 0; index < count; index++)
            {
                ProcessorPowerInformation info = Marshal.PtrToStructure<ProcessorPowerInformation>(IntPtr.Add(buffer, index * itemSize));
                if (info.CurrentMhz > 0)
                {
                    total += info.CurrentMhz;
                    samples++;
                }
            }

            return samples == 0 ? 0 : (uint)(total / samples);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool IsVirtualizationFirmwareEnabled() => IsProcessorFeaturePresent(PfVirtFirmwareEnabled);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct FileTime
{
    public readonly uint LowDateTime;
    public readonly uint HighDateTime;

    public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public static MemoryStatusEx Create()
    {
        MemoryStatusEx status = new();
        status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        return status;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PerformanceInformation
{
    public int cb;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public int HandleCount;
    public int ProcessCount;
    public int ThreadCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessorPerformanceInformation
{
    public long IdleTime;
    public long KernelTime;
    public long UserTime;
    public long DpcTime;
    public long InterruptTime;
    public uint InterruptCount;
}

internal enum LogicalProcessorRelationship
{
    ProcessorCore = 0,
    NumaNode = 1,
    Cache = 2,
    ProcessorPackage = 3,
    Group = 4
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemLogicalProcessorInformation
{
    public UIntPtr ProcessorMask;
    public LogicalProcessorRelationship Relationship;
    public ProcessorInformationUnion ProcessorInformation;
}

[StructLayout(LayoutKind.Explicit)]
internal struct ProcessorInformationUnion
{
    [FieldOffset(0)]
    public CacheDescriptor Cache;

    [FieldOffset(0)]
    private readonly ulong _reserved1;

    [FieldOffset(8)]
    private readonly ulong _reserved2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CacheDescriptor
{
    public byte Level;
    public byte Associativity;
    public ushort LineSize;
    public uint Size;
    public uint Type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessorPowerInformation
{
    public uint Number;
    public uint MaxMhz;
    public uint CurrentMhz;
    public uint MhzLimit;
    public uint MaxIdleState;
    public uint CurrentIdleState;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessBasicInformation
{
    public IntPtr Reserved1;
    public IntPtr PebBaseAddress;
    public IntPtr Reserved2A;
    public IntPtr Reserved2B;
    public IntPtr UniqueProcessId;
    public IntPtr InheritedFromUniqueProcessId;
}

internal enum TokenInformationClass
{
    TokenUser = 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct TokenUser
{
    public SidAndAttributes User;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SidAndAttributes
{
    public IntPtr Sid;
    public int Attributes;
}

internal enum SidNameUse
{
    User = 1,
    Group,
    Domain,
    Alias,
    WellKnownGroup,
    DeletedAccount,
    Invalid,
    Unknown,
    Computer,
    Label
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct EnumServiceStatusProcess
{
    public IntPtr lpServiceName;
    public IntPtr lpDisplayName;
    public ServiceStatusProcess ServiceStatusProcess;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusProcess
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
    public uint dwProcessId;
    public uint dwServiceFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatus
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ServiceDescription
{
    public IntPtr lpDescription;
}
