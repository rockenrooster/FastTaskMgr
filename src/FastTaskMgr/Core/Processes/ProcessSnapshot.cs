using System.Runtime.InteropServices;

namespace FastTaskMgr.Core.Processes;

internal sealed record SystemProcessEntry(
    int ProcessId,
    int ParentProcessId,
    string Name,
    int SessionId,
    int ThreadCount,
    int HandleCount,
    long WorkingSetBytes,
    long CreateTime,
    ulong CpuTime100ns,
    ulong IoTransferBytes,
    int BasePriority,
    bool IsSuspended);

/// <summary>
/// Reads all process rows in a single NtQuerySystemInformation(SystemProcessInformation) call.
/// The native buffer is reused across samples; callers must serialize access.
/// </summary>
internal sealed class SystemProcessReader : IDisposable
{
    private const int SystemProcessInformationClass = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int ThreadStateWait = 5;
    private const int ThreadWaitReasonSuspended = 5;

    private IntPtr _buffer;
    private int _bufferLength;

    public List<SystemProcessEntry> Read()
    {
        List<SystemProcessEntry> entries = new(256);
        if (!FillBuffer(out int dataLength))
        {
            return entries;
        }

        int processSize = Marshal.SizeOf<SystemProcessInformation>();
        int threadSize = Marshal.SizeOf<SystemThreadInformation>();
        int offset = 0;
        while (offset + processSize <= dataLength)
        {
            IntPtr entryPtr = IntPtr.Add(_buffer, offset);
            SystemProcessInformation info = Marshal.PtrToStructure<SystemProcessInformation>(entryPtr);
            int pid = info.UniqueProcessId.ToInt32();
            entries.Add(new SystemProcessEntry(
                pid,
                info.InheritedFromUniqueProcessId.ToInt32(),
                ImageName(info, pid),
                (int)info.SessionId,
                (int)info.NumberOfThreads,
                (int)info.HandleCount,
                (long)Math.Min(long.MaxValue, info.WorkingSetSize.ToUInt64()),
                info.CreateTime,
                (ulong)(info.KernelTime + info.UserTime),
                (ulong)(info.ReadTransferCount + info.WriteTransferCount),
                info.BasePriority,
                pid != 0 && AllThreadsSuspended(entryPtr, processSize, threadSize, info.NumberOfThreads, offset, dataLength)));

            if (info.NextEntryOffset == 0)
            {
                break;
            }

            offset += (int)info.NextEntryOffset;
        }

        return entries;
    }

    private bool FillBuffer(out int dataLength)
    {
        dataLength = 0;
        if (_buffer == IntPtr.Zero)
        {
            Grow(1024 * 1024);
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            int status = NtQuerySystemInformation(SystemProcessInformationClass, _buffer, _bufferLength, out int returnLength);
            if (status >= 0)
            {
                dataLength = Math.Min(returnLength, _bufferLength);
                return true;
            }

            if (status != StatusInfoLengthMismatch)
            {
                return false;
            }

            Grow(Math.Max(_bufferLength * 2, returnLength + 64 * 1024));
        }

        return false;
    }

    private void Grow(int length)
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferLength = 0;
        }

        _buffer = Marshal.AllocHGlobal(length);
        _bufferLength = length;
    }

    private static string ImageName(in SystemProcessInformation info, int pid)
    {
        if (info.ImageName.Buffer == IntPtr.Zero || info.ImageName.Length == 0)
        {
            return pid == 0 ? "System Idle Process" : $"PID {pid}";
        }

        string name = Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private bool AllThreadsSuspended(IntPtr entryPtr, int processSize, int threadSize, uint threadCount, int entryOffset, int dataLength)
    {
        if (threadCount == 0 || entryOffset + processSize + (long)threadCount * threadSize > dataLength)
        {
            return false;
        }

        for (int index = 0; index < threadCount; index++)
        {
            SystemThreadInformation thread = Marshal.PtrToStructure<SystemThreadInformation>(
                IntPtr.Add(entryPtr, processSize + index * threadSize));
            if (thread.ThreadState != ThreadStateWait || thread.WaitReason != ThreadWaitReasonSuspended)
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferLength = 0;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public NativeUnicodeString ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemThreadInformation
    {
        public long KernelTime;
        public long UserTime;
        public long CreateTime;
        public uint WaitTime;
        public IntPtr StartAddress;
        public IntPtr ClientIdProcess;
        public IntPtr ClientIdThread;
        public int Priority;
        public int BasePriority;
        public uint ContextSwitches;
        public uint ThreadState;
        public uint WaitReason;
    }
}

/// <summary>
/// One EnumWindows pass per sample builds the "has a visible window" and "is hung" sets
/// for every process, replacing per-process MainWindowHandle/Responding scans.
/// </summary>
internal static class WindowSnapshot
{
    public static (HashSet<int> WindowedPids, HashSet<int> HungPids) Query()
    {
        HashSet<int> windowed = [];
        HashSet<int> hung = [];
        _ = EnumWindows((hwnd, lparam) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                windowed.Add((int)pid);
                if (IsHungAppWindow(hwnd))
                {
                    hung.Add((int)pid);
                }
            }

            return true;
        }, IntPtr.Zero);

        return (windowed, hung);
    }

    private const int GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hwnd);
}
