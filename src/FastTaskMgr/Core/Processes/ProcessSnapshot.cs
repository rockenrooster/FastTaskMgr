using System.Runtime.InteropServices;

namespace FastTaskMgr.Core.Processes;

/// <summary>One process from the snapshot. The image name stays in the reader's buffer
/// (see <see cref="SystemProcessReader.ReadName"/>) so steady-state samples allocate no strings.</summary>
internal readonly record struct SystemProcessEntry(
    int ProcessId,
    int ParentProcessId,
    int SessionId,
    int ThreadCount,
    int HandleCount,
    long WorkingSetBytes,
    long CreateTime,
    ulong CpuTime100ns,
    ulong IoTransferBytes,
    int BasePriority,
    bool IsSuspended,
    int NameOffset,
    int NameLength);

/// <summary>
/// Reads all process rows in a single NtQuerySystemInformation(SystemProcessInformation) call.
/// The buffer is reused across samples; callers must serialize access and consume entries
/// (including names) before the next read.
/// </summary>
internal sealed class SystemProcessReader : IDisposable
{
    private const int SystemProcessInformationClass = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int ThreadStateWait = 5;
    private const int ThreadWaitReasonSuspended = 5;
    private static readonly int ProcessSize = Marshal.SizeOf<SystemProcessInformation>();
    private static readonly int ThreadSize = Marshal.SizeOf<SystemThreadInformation>();

    // Pinned managed buffer: entries are read with MemoryMarshal (no boxing per struct, unlike
    // Marshal.PtrToStructure) and nothing native is left to free if a read is in flight at exit.
    private byte[] _buffer = [];
    private IntPtr _bufferAddress;
    private int _dataLength;

    public void Read(List<SystemProcessEntry> entries)
    {
        entries.Clear();
        if (!FillBuffer())
        {
            return;
        }

        ReadOnlySpan<byte> data = _buffer.AsSpan(0, _dataLength);
        int offset = 0;
        while (offset + ProcessSize <= data.Length)
        {
            SystemProcessInformation info = MemoryMarshal.Read<SystemProcessInformation>(data[offset..]);
            int pid = (int)info.UniqueProcessId;
            (int nameOffset, int nameLength) = NameRange(info, data.Length);
            entries.Add(new SystemProcessEntry(
                pid,
                (int)info.InheritedFromUniqueProcessId,
                (int)info.SessionId,
                (int)info.NumberOfThreads,
                (int)info.HandleCount,
                (long)Math.Min(long.MaxValue, info.WorkingSetSize.ToUInt64()),
                info.CreateTime,
                (ulong)(info.KernelTime + info.UserTime),
                (ulong)(info.ReadTransferCount + info.WriteTransferCount),
                info.BasePriority,
                pid != 0 && AllThreadsSuspended(data, offset + ProcessSize, info.NumberOfThreads),
                nameOffset,
                nameLength));

            if (info.NextEntryOffset == 0)
            {
                break;
            }

            offset += (int)info.NextEntryOffset;
        }
    }

    /// <summary>Materializes an entry's display name from the current snapshot.</summary>
    public string ReadName(in SystemProcessEntry entry)
    {
        if (entry.NameLength <= 0)
        {
            return entry.ProcessId == 0 ? "System Idle Process" : $"PID {entry.ProcessId}";
        }

        ReadOnlySpan<char> name = MemoryMarshal.Cast<byte, char>(_buffer.AsSpan(entry.NameOffset, entry.NameLength));
        return new string(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
    }

    // The kernel stores image names inside the same buffer, after the entries.
    private (int Offset, int Length) NameRange(in SystemProcessInformation info, int dataLength)
    {
        long offset = (long)info.ImageName.Buffer - (long)_bufferAddress;
        int length = info.ImageName.Length & ~1;
        return info.ImageName.Buffer == IntPtr.Zero || length == 0 || offset < 0 || offset + length > dataLength
            ? (0, 0)
            : ((int)offset, length);
    }

    private bool FillBuffer()
    {
        _dataLength = 0;
        if (_buffer.Length == 0)
        {
            Grow(1024 * 1024);
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            int status = NtQuerySystemInformation(SystemProcessInformationClass, _bufferAddress, _buffer.Length, out int returnLength);
            if (status >= 0)
            {
                _dataLength = Math.Min(returnLength, _buffer.Length);
                return true;
            }

            if (status != StatusInfoLengthMismatch)
            {
                return false;
            }

            Grow(Math.Max(_buffer.Length * 2, returnLength + 64 * 1024));
        }

        return false;
    }

    private void Grow(int length)
    {
        _buffer = GC.AllocateUninitializedArray<byte>(length, pinned: true);
        _bufferAddress = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0);
    }

    private static bool AllThreadsSuspended(ReadOnlySpan<byte> data, int firstThreadOffset, uint threadCount)
    {
        if (threadCount == 0 || firstThreadOffset + (long)threadCount * ThreadSize > data.Length)
        {
            return false;
        }

        for (int index = 0; index < threadCount; index++)
        {
            SystemThreadInformation thread = MemoryMarshal.Read<SystemThreadInformation>(data[(firstThreadOffset + index * ThreadSize)..]);
            if (thread.ThreadState != ThreadStateWait || thread.WaitReason != ThreadWaitReasonSuspended)
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        _buffer = [];
        _bufferAddress = IntPtr.Zero;
        _dataLength = 0;
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
/// One EnumWindows pass per sample finds every process with a hung top-level window,
/// replacing per-process MainWindowHandle/Responding scans.
/// </summary>
internal static class WindowSnapshot
{
    public static HashSet<int> QueryHungProcessIds()
    {
        HashSet<int> hung = [];
        _ = EnumWindows((hwnd, lparam) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0 && IsHungAppWindow(hwnd))
            {
                hung.Add((int)pid);
            }

            return true;
        }, IntPtr.Zero);

        return hung;
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
