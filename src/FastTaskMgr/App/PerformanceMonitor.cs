using FastTaskMgr.Core.Performance;
using FastTaskMgr.Diagnostics;

namespace FastTaskMgr.App;

/// <summary>
/// Samples system performance on the update-speed timer from startup, whichever page is shown
/// and also while the window is minimized or hidden in the tray, so the Performance graphs
/// always hold recent history, like Task Manager. Lives on the UI thread; each sample runs on a
/// pool thread and costs about a millisecond.
/// </summary>
internal sealed class PerformanceMonitor : IDisposable
{
    public const int HistoryLength = 120;

    private readonly AppState _state;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Dictionary<string, List<double>> _history = [];
    private bool _started;
    private bool _sampling;

    public PerformanceMonitor(AppState state)
    {
        _state = state;
        _timer.Tick += async (_, _) =>
        {
            // Holding Ctrl in FastTaskMgr freezes live updates, like Task Manager. Only while it
            // is the foreground window: a background app's key state is not kept current, so a
            // stale Ctrl could otherwise stop sampling while minimized.
            if (Form.ActiveForm is null || (Control.ModifierKeys & Keys.Control) != Keys.Control)
            {
                await SampleAsync();
            }
        };
        _state.SettingsChanged += (_, _) => UpdateTimer();
    }

    /// <summary>Most recent sample, or null before the first one completes.</summary>
    public PerformanceSample? Latest { get; private set; }

    /// <summary>Raised on the UI thread after each sample is recorded.</summary>
    public event EventHandler? Sampled;

    /// <summary>Last <see cref="HistoryLength"/> points (0-100) of a series such as "cpu" or a disk key.</summary>
    public IReadOnlyList<double> History(string key) => _history.TryGetValue(key, out List<double>? values) ? values : [];

    /// <summary>
    /// Starts timed sampling. The first full sample also pays the PDH warm-up (over a second
    /// on first touch) in the background, before the Performance page is likely opened.
    /// </summary>
    public void Start()
    {
        _started = true;
        UpdateTimer();
        _ = SampleAsync();
    }

    /// <summary>
    /// Takes a sample now unless one is in flight. With <paramref name="fastFirstPaint"/>, a
    /// sample taken before PDH is warmed up skips the PDH counters and returns at once.
    /// </summary>
    public async Task SampleAsync(bool fastFirstPaint = false)
    {
        if (_sampling)
        {
            return;
        }

        _sampling = true;
        try
        {
            PerformanceSample sample = await Task.Run(() => _state.Performance.Sample(fastFirstPaint));
            Record(sample);
            Sampled?.Invoke(this, EventArgs.Empty);
        }
        catch (ObjectDisposedException)
        {
            // The sampler was released during shutdown.
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Performance sampling", ex);
        }
        finally
        {
            _sampling = false;
        }
    }

    private void Record(PerformanceSample sample)
    {
        Latest = sample;
        Add("cpu", sample.CpuPercent);
        for (int index = 0; index < sample.CpuCorePercents.Count; index++)
        {
            Add($"cpu-core:{index}", sample.CpuCorePercents[index]);
        }

        Add("memory", MemoryPercent(sample));
        foreach (DiskPerformanceSample disk in sample.Disks)
        {
            Add(disk.Key, disk.ActivePercent);
            foreach (DiskPerformanceSample member in disk.Members ?? [])
            {
                Add(member.Key, member.ActivePercent);
            }
        }

        foreach (NetworkPerformanceSample network in sample.Networks)
        {
            Add(network.Key, network.UtilizationPercent);
        }

        foreach (GpuPerformanceSample gpu in sample.Gpus)
        {
            Add(gpu.Key, gpu.UtilizationPercent);
        }
    }

    public static double MemoryPercent(PerformanceSample sample) => sample.TotalMemoryBytes == 0
        ? 0
        : (double)(sample.TotalMemoryBytes - sample.AvailableMemoryBytes) / sample.TotalMemoryBytes * 100;

    private void Add(string key, double value)
    {
        if (!_history.TryGetValue(key, out List<double>? samples))
        {
            samples = new List<double>(HistoryLength + 1);
            _history[key] = samples;
        }

        samples.Add(Math.Clamp(value, 0, 100));
        if (samples.Count > HistoryLength)
        {
            samples.RemoveAt(0);
        }
    }

    private void UpdateTimer()
    {
        int interval = _state.Settings.UpdateSpeed.ToMilliseconds();
        if (!_started || interval <= 0)
        {
            _timer.Stop();
            return;
        }

        if (!_timer.Enabled || _timer.Interval != interval)
        {
            _timer.Stop();
            _timer.Interval = interval;
            _timer.Start();
        }
    }

    public void Dispose() => _timer.Dispose();
}
