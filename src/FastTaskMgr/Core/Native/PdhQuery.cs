using System.Runtime.InteropServices;

namespace FastTaskMgr.Core.Native;

/// <summary>
/// One PDH query holding wildcard counters, collected once per sample. Replaces
/// per-instance System.Diagnostics.PerformanceCounter objects, whose NextValue each
/// re-reads the whole category (hundreds of reads per tick for GPU engines).
/// Not thread-safe; the owner serializes access.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    // PDH_FMT_COUNTERVALUE_ITEM_W on x64: LPWSTR szName (8), then PDH_FMT_COUNTERVALUE
    // { DWORD CStatus (4) + pad (4), double (8) } => 24 bytes per item.
    private const int ItemSize = 24;

    private IntPtr _query;
    // Grow-only pinned buffer reused by every array read. PDH writes the instance names into
    // the same buffer, so they can be read as spans instead of allocating a string per instance
    // (hundreds per tick for GPU engines).
    private byte[] _buffer = [];
    private IntPtr _bufferAddress;

    public delegate void ArrayValueVisitor(ReadOnlySpan<char> instance, double value);

    public PdhQuery()
    {
        if (PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out IntPtr query) == 0)
        {
            _query = query;
        }
    }

    public bool IsValid => _query != IntPtr.Zero;

    /// <summary>Adds a locale-independent counter path; returns zero when unsupported.</summary>
    public IntPtr AddCounter(string path)
    {
        if (!IsValid || PdhAddEnglishCounter(_query, path, IntPtr.Zero, out IntPtr counter) != 0)
        {
            return IntPtr.Zero;
        }

        return counter;
    }

    public bool Collect() => IsValid && PdhCollectQueryData(_query) == 0;

    /// <summary>Reads a wildcard counter as (instance, value) pairs. Empty until two collections have run.</summary>
    public List<(string Instance, double Value)> ReadArray(IntPtr counter)
    {
        List<(string, double)> values = [];
        VisitArray(counter, (instance, value) => values.Add((instance.ToString(), value)));
        return values;
    }

    /// <summary>
    /// Visits each valid instance of a wildcard counter. The instance span is only valid during
    /// the callback. Nothing is visited until two collections have run.
    /// </summary>
    public void VisitArray(IntPtr counter, ArrayValueVisitor visitor)
    {
        if (counter == IntPtr.Zero)
        {
            return;
        }

        int bufferSize = 0;
        uint status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, out _, IntPtr.Zero);
        if (status != PdhMoreData || bufferSize <= 0)
        {
            return;
        }

        if (_buffer.Length < bufferSize)
        {
            _buffer = GC.AllocateUninitializedArray<byte>(Math.Max(bufferSize, _buffer.Length * 2), pinned: true);
            _bufferAddress = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0);
        }

        bufferSize = _buffer.Length;
        status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, out int count, _bufferAddress);
        if (status != 0)
        {
            return;
        }

        Span<byte> data = _buffer.AsSpan(0, Math.Min(bufferSize, _buffer.Length));
        for (int index = 0; index < count && (index + 1) * ItemSize <= data.Length; index++)
        {
            Span<byte> item = data.Slice(index * ItemSize, ItemSize);
            IntPtr namePtr = MemoryMarshal.Read<IntPtr>(item);
            int valueStatus = MemoryMarshal.Read<int>(item[8..]);
            if (namePtr == IntPtr.Zero || valueStatus != 0)
            {
                continue;
            }

            double value = MemoryMarshal.Read<double>(item[16..]);
            long nameOffset = (long)namePtr - (long)_bufferAddress;
            if (nameOffset >= 0 && nameOffset < data.Length)
            {
                ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(data[(int)nameOffset..]);
                int terminator = chars.IndexOf('\0');
                visitor(terminator < 0 ? chars : chars[..terminator], value);
            }
            else
            {
                visitor(Marshal.PtrToStringUni(namePtr) ?? "", value);
            }
        }
    }

    /// <summary>Reads a single (non-wildcard) counter value; null until valid.</summary>
    public double? ReadValue(IntPtr counter)
    {
        if (counter == IntPtr.Zero
            || PdhGetFormattedCounterValue(counter, PdhFmtDouble, IntPtr.Zero, out FormattedDoubleValue value) != 0
            || value.CStatus != 0)
        {
            return null;
        }

        return value.Value;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    // PDH_FMT_COUNTERVALUE with the double member of its union.
    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedDoubleValue
    {
        public uint CStatus;
        public double Value;
    }

    [DllImport("pdh.dll")]
    private static extern uint PdhOpenQuery(IntPtr szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr hCounter, uint dwFormat, ref int lpdwBufferSize, out int lpdwItemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, out FormattedDoubleValue pValue);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr hQuery);
}
