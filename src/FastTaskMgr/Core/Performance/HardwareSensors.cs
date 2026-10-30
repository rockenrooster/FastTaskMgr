using FastTaskMgr.Core.Native;
using FastTaskMgr.Diagnostics;

namespace FastTaskMgr.Core.Performance;

/// <summary>What the sensors reported so far. The Read flags tell "not read yet" from "not available".</summary>
internal sealed record HardwareReadings(
    bool TemperaturesRead,
    double? CpuTemperatureCelsius,
    double? GpuTemperatureCelsius,
    bool DisksRead,
    IReadOnlyDictionary<int, StorageHealth> Disks)
{
    public static readonly HardwareReadings None = new(false, null, null, false, new Dictionary<int, StorageHealth>());
}

/// <summary>
/// Reads temperatures and SSD health on its own background loop, only while something shows them.
/// Nothing here runs at startup, and a slow or stuck drive can never delay a performance sample
/// or the UI: readings simply arrive when they are ready.
/// </summary>
internal sealed class HardwareSensors : IDisposable
{
    private const int TemperatureIntervalMs = 2000;
    private const long DiskIntervalMs = 10_000; // each read is a command to the drive; temperatures move slowly
    private const int MaxDiskIndex = 63;
    private const string ThermalZonePath = @"\Thermal Zone Information(*)\High Precision Temperature";

    private CancellationTokenSource? _cancellation;
    private bool _continuous;
    private Task _loop = Task.CompletedTask;
    private volatile HardwareReadings _latest = HardwareReadings.None;

    public HardwareReadings Latest => _latest;

    /// <summary>Raised on a background thread when a reading changes.</summary>
    public event EventHandler? Updated;

    /// <summary>
    /// Starts or stops reading. With continuous false, one pass is made and polling then stops
    /// (for paused updates). Readings are kept while stopped, so reopening shows them at once.
    /// </summary>
    public void SetActive(bool active, bool continuous)
    {
        if (active == _cancellation is not null && continuous == _continuous)
        {
            return;
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _continuous = continuous;
        if (!active)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        Task previous = _loop;
        _loop = Task.Run(async () =>
        {
            // A read from the last session may still be stuck on a slow drive; never run two loops.
            await previous;
            await RunAsync(continuous, token);
        });
    }

    private async Task RunAsync(bool continuous, CancellationToken token)
    {
        try
        {
            using PdhQuery thermalZones = new();
            IntPtr thermalCounter = thermalZones.AddCounter(ThermalZonePath);
            long disksDueAt = 0;
            while (!token.IsCancellationRequested)
            {
                ReadTemperatures(thermalZones, thermalCounter);
                if (Environment.TickCount64 >= disksDueAt)
                {
                    ReadDisks(token);
                    disksDueAt = Environment.TickCount64 + DiskIntervalMs;
                }

                if (!continuous)
                {
                    return;
                }

                await Task.Delay(TemperatureIntervalMs, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Hardware sensors", ex);
        }
    }

    private void ReadTemperatures(PdhQuery thermalZones, IntPtr thermalCounter)
    {
        double? cpu = ReadThermalZoneCelsius(thermalZones, thermalCounter);
        double? gpu = NativeMethods.QueryGpuTemperatureCelsius();
        Publish(_latest with { TemperaturesRead = true, CpuTemperatureCelsius = cpu, GpuTemperatureCelsius = gpu });
    }

    // ACPI thermal zones are the only CPU-related temperature Windows exposes without a kernel
    // driver. Laptops usually have one or more; many desktops have none. Takes the hottest.
    private static double? ReadThermalZoneCelsius(PdhQuery thermalZones, IntPtr counter)
    {
        if (!thermalZones.Collect())
        {
            return null;
        }

        double? hottest = null;
        thermalZones.VisitArray(counter, (_, tenthsOfKelvin) =>
        {
            double celsius = tenthsOfKelvin / 10 - 273.15;
            if (celsius is > 0 and < 150)
            {
                hottest = Math.Max(hottest ?? 0, celsius);
            }
        });
        return hottest;
    }

    private void ReadDisks(CancellationToken token)
    {
        Dictionary<int, StorageHealth> disks = [];
        for (int index = 0; index <= MaxDiskIndex && !token.IsCancellationRequested; index++)
        {
            if (NativeMethods.QueryStorageHealth(index) is { } health)
            {
                disks[index] = health;
            }
        }

        if (!token.IsCancellationRequested)
        {
            Publish(_latest with { DisksRead = true, Disks = disks });
        }
    }

    private void Publish(HardwareReadings readings)
    {
        bool changed = readings.TemperaturesRead != _latest.TemperaturesRead
            || readings.DisksRead != _latest.DisksRead
            || readings.CpuTemperatureCelsius != _latest.CpuTemperatureCelsius
            || readings.GpuTemperatureCelsius != _latest.GpuTemperatureCelsius
            || !readings.Disks.SequenceEqual(_latest.Disks);
        _latest = readings;
        if (changed)
        {
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    // Bounded wait: exiting should not hang on a stuck drive, and the loop holds nothing that
    // process exit does not clean up.
    public void Dispose()
    {
        SetActive(false, continuous: false);
        try
        {
            _ = _loop.Wait(500);
        }
        catch (AggregateException)
        {
        }
    }
}
