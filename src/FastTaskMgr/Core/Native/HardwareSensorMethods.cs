using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FastTaskMgr.Core.Native;

/// <summary>Temperature and remaining life of an SSD; either can be missing if the drive does not report it.</summary>
internal readonly record struct StorageHealth(double? TemperatureCelsius, int? LifeRemainingPercent);

internal static partial class NativeMethods
{
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const int BusTypeSata = 11;
    private const int BusTypeNvme = 17;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int StorageDeviceTemperatureProperty = 52;
    private const int ProtocolTypeAta = 2;
    private const int ProtocolTypeNvme = 3;
    private const int ProtocolDataTypeLogPage = 2; // NVMeDataTypeLogPage and AtaDataTypeLogPage
    private const int NvmeLogPageHealthInfo = 2;
    private const int AtaLogDeviceStatistics = 4;
    private const int AtaStatisticsPageTemperature = 5;
    private const int AtaStatisticsPageSolidState = 7;
    private const int LogPageLength = 512;
    private const int PropertyQueryHeaderLength = 8; // STORAGE_PROPERTY_QUERY up to AdditionalParameters
    private const int ProtocolDataLength = 40; // STORAGE_PROTOCOL_SPECIFIC_DATA
    private const int ProtocolDescriptorHeaderLength = 8; // STORAGE_PROTOCOL_DATA_DESCRIPTOR up to the protocol data

    private const int KmtQueryAdapterPerfData = 62; // KMTQAITYPE_ADAPTERPERFDATA
    private const int MaxGpuAdapters = 16;
    private const int AdapterPerfDataLength = 64; // D3DKMT_ADAPTER_PERFDATA
    private const int AdapterPerfDataTemperatureOffset = 56;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtEnumAdapters2
    {
        public uint NumAdapters;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtAdapterInfo
    {
        public uint Adapter;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtQueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr PrivateDriverData;
        public int PrivateDriverDataSize;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3dkmtEnumAdapters2 data);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo data);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref uint adapter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeNativeHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// Hottest temperature any GPU reports through WDDM, the source Task Manager uses. Needs no
    /// elevation or vendor library. Null when no adapter reports one (integrated GPUs usually do not).
    /// </summary>
    public static double? QueryGpuTemperatureCelsius()
    {
        int infoSize = Marshal.SizeOf<D3dkmtAdapterInfo>();
        IntPtr adapters = Marshal.AllocHGlobal(infoSize * MaxGpuAdapters);
        IntPtr perfData = Marshal.AllocHGlobal(AdapterPerfDataLength);
        try
        {
            D3dkmtEnumAdapters2 enumeration = new() { NumAdapters = MaxGpuAdapters, Adapters = adapters };
            if (D3DKMTEnumAdapters2(ref enumeration) != 0)
            {
                return null;
            }

            double? hottest = null;
            for (int index = 0; index < enumeration.NumAdapters; index++)
            {
                uint adapter = Marshal.PtrToStructure<D3dkmtAdapterInfo>(IntPtr.Add(adapters, index * infoSize)).Adapter;
                Marshal.WriteInt32(perfData, 0, 0); // PhysicalAdapterIndex: the first GPU of the adapter
                D3dkmtQueryAdapterInfo query = new()
                {
                    Adapter = adapter,
                    Type = KmtQueryAdapterPerfData,
                    PrivateDriverData = perfData,
                    PrivateDriverDataSize = AdapterPerfDataLength
                };

                // Software adapters fail this query; a reported 0 means the GPU has no sensor.
                if (D3DKMTQueryAdapterInfo(ref query) == 0)
                {
                    int deciCelsius = Marshal.ReadInt32(perfData, AdapterPerfDataTemperatureOffset);
                    if (deciCelsius > 0)
                    {
                        hottest = Math.Max(hottest ?? 0, deciCelsius / 10d);
                    }
                }

                _ = D3DKMTCloseAdapter(ref adapter);
            }

            return hottest;
        }
        finally
        {
            Marshal.FreeHGlobal(adapters);
            Marshal.FreeHGlobal(perfData);
        }
    }

    /// <summary>
    /// Reads an internal SSD's temperature and remaining life. Null for anything else: hard
    /// drives are left alone so polling can never spin one up, and virtual or USB disks do not
    /// report these. Works without elevation because the device is opened with no access rights.
    /// </summary>
    public static StorageHealth? QueryStorageHealth(int diskIndex)
    {
        const uint fileShareReadWrite = 0x3;
        const uint openExisting = 3;

        using SafeNativeHandle device = CreateFile($@"\\.\PhysicalDrive{diskIndex}", 0, fileShareReadWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (device.IsInvalid)
        {
            return null;
        }

        return QueryBusType(device) switch
        {
            BusTypeNvme => ReadNvmeHealth(device),
            BusTypeSata when IsConfirmedSolidState(device) => ReadSataHealth(device),
            _ => null
        };
    }

    /// <summary>Decodes an NVMe SMART / Health Information log page (NVME_HEALTH_INFO_LOG).</summary>
    internal static StorageHealth ParseNvmeHealthLog(ReadOnlySpan<byte> log)
    {
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        int percentageUsed = log[5]; // may exceed 100 once the rated endurance is passed
        return new StorageHealth(kelvin == 0 ? null : kelvin - 273.15, Math.Max(0, 100 - percentageUsed));
    }

    /// <summary>Decodes one statistic from an ATA Device Statistics page; null unless flagged supported and valid.</summary>
    internal static int? ParseAtaStatistic(ReadOnlySpan<byte> page, int offset)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(page[offset..]);
        return value >> 62 == 3 ? (sbyte)(value & 0xFF) : null;
    }

    private static StorageHealth ReadNvmeHealth(SafeNativeHandle device)
    {
        byte[]? log = QueryLogPage(device, ProtocolTypeNvme, NvmeLogPageHealthInfo, 0);
        return log is null
            ? new StorageHealth(QueryTemperatureProperty(device), null)
            : ParseNvmeHealthLog(log);
    }

    private static StorageHealth ReadSataHealth(SafeNativeHandle device)
    {
        double? temperature = QueryTemperatureProperty(device);
        if (temperature is null && QueryLogPage(device, ProtocolTypeAta, AtaLogDeviceStatistics, AtaStatisticsPageTemperature) is { } temperaturePage)
        {
            temperature = ParseAtaStatistic(temperaturePage, 8);
        }

        int? percentageUsed = QueryLogPage(device, ProtocolTypeAta, AtaLogDeviceStatistics, AtaStatisticsPageSolidState) is { } endurancePage
            ? ParseAtaStatistic(endurancePage, 8)
            : null;
        return new StorageHealth(temperature, percentageUsed is null ? null : Math.Max(0, 100 - percentageUsed.Value));
    }

    private static byte[]? QueryProperty(SafeNativeHandle device, int propertyId, int outputLength)
    {
        byte[] input = new byte[PropertyQueryHeaderLength + sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(input, propertyId);
        byte[] output = new byte[outputLength];
        return DeviceIoControl(device, IoctlStorageQueryProperty, input, input.Length, output, output.Length, out int returned, IntPtr.Zero) && returned > 0
            ? output
            : null;
    }

    private static int QueryBusType(SafeNativeHandle device)
    {
        const int busTypeOffset = 28; // STORAGE_DEVICE_DESCRIPTOR.BusType
        byte[]? descriptor = QueryProperty(device, StorageDeviceProperty, 64);
        return descriptor is null ? -1 : BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(busTypeOffset));
    }

    // Unknown counts as "not an SSD": some SATA drivers do not answer this, and hard drives are
    // never polled.
    private static bool IsConfirmedSolidState(SafeNativeHandle device)
    {
        const int incursSeekPenaltyOffset = 8; // DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty
        return QueryProperty(device, StorageDeviceSeekPenaltyProperty, 12) is { } descriptor
            && descriptor[incursSeekPenaltyOffset] == 0;
    }

    private static double? QueryTemperatureProperty(SafeNativeHandle device)
    {
        const int infoCountOffset = 12; // STORAGE_TEMPERATURE_DATA_DESCRIPTOR.InfoCount
        const int firstTemperatureOffset = 26; // TemperatureInfo[0].Temperature, in Celsius
        byte[]? descriptor = QueryProperty(device, StorageDeviceTemperatureProperty, 128);
        return descriptor is not null && BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(infoCountOffset)) > 0
            ? BinaryPrimitives.ReadInt16LittleEndian(descriptor.AsSpan(firstTemperatureOffset))
            : null;
    }

    // The request is a STORAGE_PROPERTY_QUERY whose additional parameters are a
    // STORAGE_PROTOCOL_SPECIFIC_DATA; the reply reuses the buffer as a data descriptor.
    private static byte[]? QueryLogPage(SafeNativeHandle device, int protocolType, int logPage, int subPage)
    {
        const int requestDataOffset = PropertyQueryHeaderLength;
        const int replyDataOffsetField = ProtocolDescriptorHeaderLength + 16; // ProtocolDataOffset
        byte[] buffer = new byte[PropertyQueryHeaderLength + ProtocolDataLength + LogPageLength];
        Span<byte> request = buffer.AsSpan(requestDataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(buffer, StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(request, protocolType);
        BinaryPrimitives.WriteInt32LittleEndian(request[4..], ProtocolDataTypeLogPage);
        BinaryPrimitives.WriteInt32LittleEndian(request[8..], logPage);
        BinaryPrimitives.WriteInt32LittleEndian(request[12..], subPage);
        BinaryPrimitives.WriteInt32LittleEndian(request[16..], ProtocolDataLength);
        BinaryPrimitives.WriteInt32LittleEndian(request[20..], LogPageLength);
        if (!DeviceIoControl(device, IoctlStorageQueryProperty, buffer, buffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero))
        {
            return null;
        }

        int dataOffset = ProtocolDescriptorHeaderLength + BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(replyDataOffsetField));
        return dataOffset >= ProtocolDescriptorHeaderLength && dataOffset + LogPageLength <= Math.Min(returned, buffer.Length)
            ? buffer.AsSpan(dataOffset, LogPageLength).ToArray()
            : null;
    }
}
