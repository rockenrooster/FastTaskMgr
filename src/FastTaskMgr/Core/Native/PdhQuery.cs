using System.Runtime.InteropServices;

namespace FastTaskMgr.Core.Native;

/// <summary>
/// One PDH query holding wildcard counters, collected once per sample. Replaces
/// per-instance System.Diagnostics.PerformanceCounter objects, whose NextValue each
/// re-reads the whole category (hundreds of reads per tick for GPU engines).
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;

    private IntPtr _query;

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
        if (counter == IntPtr.Zero)
        {
            return values;
        }

        int bufferSize = 0;
        uint status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, out _, IntPtr.Zero);
        if (status != PdhMoreData || bufferSize <= 0)
        {
            return values;
        }

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, out int count, buffer);
            if (status != 0)
            {
                return values;
            }

            // PDH_FMT_COUNTERVALUE_ITEM_W on x64: LPWSTR szName (8), then PDH_FMT_COUNTERVALUE
            // { DWORD CStatus (4) + pad (4), double (8) } => 24 bytes per item.
            const int itemSize = 24;
            for (int index = 0; index < count; index++)
            {
                IntPtr item = IntPtr.Add(buffer, index * itemSize);
                IntPtr namePtr = Marshal.ReadIntPtr(item, 0);
                int valueStatus = Marshal.ReadInt32(item, 8);
                if (namePtr == IntPtr.Zero || valueStatus != 0)
                {
                    continue;
                }

                string name = Marshal.PtrToStringUni(namePtr) ?? "";
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                values.Add((name, value));
            }

            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads a single (non-wildcard) counter value; null until valid.</summary>
    public double? ReadValue(IntPtr counter)
    {
        if (counter == IntPtr.Zero)
        {
            return null;
        }

        // PDH_FMT_COUNTERVALUE: DWORD CStatus + pad, double value.
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try
        {
            if (PdhGetFormattedCounterValue(counter, PdhFmtDouble, IntPtr.Zero, buffer) != 0
                || Marshal.ReadInt32(buffer, 0) != 0)
            {
                return null;
            }

            return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 8));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
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
    private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, IntPtr pValue);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr hQuery);
}
