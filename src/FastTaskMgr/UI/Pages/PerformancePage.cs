using FastTaskMgr.App;
using FastTaskMgr.Core.Native;
using FastTaskMgr.Core.Performance;
using FastTaskMgr.UI.Controls;
using static FastTaskMgr.UI.UiScale;

namespace FastTaskMgr.UI.Pages;

internal sealed class PerformancePage : PageBase
{
    private static readonly Color CpuColor = Color.FromArgb(0, 120, 170);
    private static readonly Color MemoryColor = Color.FromArgb(125, 70, 170);
    private static readonly Color DiskColor = Color.FromArgb(80, 140, 45);
    private static readonly Color NetworkColor = Color.FromArgb(200, 40, 105);
    private static readonly Color GpuColor = Color.FromArgb(120, 65, 220);
    private const string ReadingText = "Reading...";

    private readonly BufferedFlowLayoutPanel _sidebar = new();
    private readonly Panel _detailHost = new();
    private readonly Label _detailTitle = new();
    private readonly Label _detailSubtitle = new();
    private readonly BufferedFlowLayoutPanel _detailStats = new();
    private readonly RowStyle _detailStatsRow = new(SizeType.Absolute, Px(150));
    private readonly PerformanceMonitor _monitor;
    private readonly HardwareSensors _sensors;
    private readonly Dictionary<string, PerfTile> _tiles = [];
    private readonly ContextMenuStrip _sidebarMenu = new();
    private readonly Dictionary<string, GraphControl> _diskMemberGraphs = [];
    private TableLayoutPanel? _memberGrid;
    private IReadOnlyList<PerfItem> _currentItems = [];
    private IReadOnlyList<StatItem> _currentStats = [];
    private GraphControl? _detailGraph;
    private GraphGridControl? _cpuGrid;
    private string _selectedKey = "cpu";
    private PerformanceSample? _latest;

    public PerformancePage(AppState state)
        : base(state)
    {
        // Sampling and history live in the monitor, which keeps running while other pages are
        // shown or the window is minimized; this page only draws, and only while on screen.
        _monitor = state.PerformanceMonitor;
        _monitor.Sampled += OnSampled;
        _sensors = state.Sensors;
        _sensors.Updated += OnSensorsUpdated;
        state.SettingsChanged += (_, _) => UpdateSensorPolling();
        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = Px(6),
            IsSplitterFixed = false
        };
        split.SizeChanged += (_, _) => ConfigureSplitter(split);
        Controls.Add(split);

        _sidebar.Dock = DockStyle.Fill;
        _sidebar.FlowDirection = FlowDirection.TopDown;
        _sidebar.WrapContents = false;
        _sidebar.AutoScroll = true;
        _sidebar.Padding = Px(new Padding(6));
        _sidebar.Resize += (_, _) => ResizeSidebarTiles();
        // Tiles and their labels have no menu of their own, so right-clicks bubble up here.
        _sidebar.ContextMenuStrip = _sidebarMenu;
        _sidebarMenu.Opening += (_, _) => BuildSidebarMenu();
        split.Panel1.Controls.Add(_sidebar);

        TableLayoutPanel detail = new()
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = Px(new Padding(8, 6, 8, 6))
        };
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(62)));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detail.RowStyles.Add(_detailStatsRow);
        detail.Resize += (_, _) => ReflowStats();
        split.Panel2.Controls.Add(detail);

        Panel header = new() { Dock = DockStyle.Fill };
        _detailTitle.Font = RelativeFont(Font, 20);
        _detailTitle.SetBounds(0, 0, Px(300), Math.Max(Px(34), TextHeight(_detailTitle.Font)));
        _detailTitle.AutoSize = false;
        _detailTitle.AutoEllipsis = true;
        _detailSubtitle.SetBounds(Px(2), _detailTitle.Bottom + Px(2), Px(300), Math.Max(Px(22), TextHeight(Font)));
        _detailSubtitle.AutoSize = false;
        _detailSubtitle.AutoEllipsis = true;
        detail.RowStyles[0].Height = Math.Max(Px(62), _detailSubtitle.Bottom + Px(4));
        header.Resize += (_, _) =>
        {
            _detailTitle.Width = header.ClientSize.Width;
            _detailSubtitle.Width = Math.Max(1, header.ClientSize.Width - Px(2));
        };
        header.Controls.Add(_detailTitle);
        header.Controls.Add(_detailSubtitle);
        detail.Controls.Add(header, 0, 0);

        _detailHost.Dock = DockStyle.Fill;
        _detailHost.Resize += (_, _) =>
        {
            if (_selectedKey == "cpu" && _latest is not null)
            {
                RenderDetail(_currentItems.FirstOrDefault(item => item.Key == "cpu"));
            }
        };
        detail.Controls.Add(_detailHost, 0, 1);

        _detailStats.Dock = DockStyle.Fill;
        _detailStats.Padding = Px(new Padding(0, 6, 0, 0));
        _detailStats.WrapContents = true;
        _detailStats.AutoScroll = true;
        _detailStats.Resize += (_, _) => ReflowStats();
        detail.Controls.Add(_detailStats, 0, 2);
    }

    public override string Title => "Performance";
    public override bool UsesSearch => false;

    protected override async void OnShow()
    {
        UpdateSensorPolling();
        if (_monitor.Latest is { } latest)
        {
            RenderSample(latest); // paint at once, with the history gathered in the background
        }

        // Before the first sample, or with updates paused (no timer), take one now. Until PDH
        // is warm, the fast path returns CPU and memory without waiting on it.
        bool paused = State.Settings.UpdateSpeed == UpdateSpeed.Paused;
        if (_monitor.Latest is null || paused)
        {
            await _monitor.SampleAsync(fastFirstPaint: _monitor.Latest is null && !paused);
        }
    }

    private void OnSampled(object? sender, EventArgs e)
    {
        if (IsActive && !IsSuspended && !IsDisposed && _monitor.Latest is { } latest)
        {
            RenderSample(latest);
        }
    }

    protected override void OnHide() => UpdateSensorPolling();

    protected override void OnSuspend() => UpdateSensorPolling();

    // Catch up at once on restore instead of showing the pre-minimize frame until the next tick.
    protected override void OnResume()
    {
        UpdateSensorPolling();
        if (_monitor.Latest is { } latest)
        {
            RenderSample(latest);
        }
    }

    // Unlike the graphs, these readings have no history, so nothing touches the sensors unless
    // they are on screen. With updates paused they are read once and then left alone.
    private void UpdateSensorPolling() =>
        _sensors.SetActive(IsActive && !IsSuspended, continuous: State.Settings.UpdateSpeed != UpdateSpeed.Paused);

    // Readings arrive on the sensor loop's thread, whenever each one is ready.
    private void OnSensorsUpdated(object? sender, EventArgs e)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (IsActive && !IsSuspended && !IsDisposed && _latest is not null)
                {
                    RenderSample(_latest);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // The window handle is gone (shutting down); nothing left to update.
        }
    }

    private static void ConfigureSplitter(SplitContainer split)
    {
        if (split.Width < Px(260))
        {
            return;
        }

        int target = Math.Clamp(split.SplitterDistance, Px(170), Math.Min(Px(260), split.Width - Px(220)));
        if (split.SplitterDistance != target)
        {
            split.SplitterDistance = target;
        }
    }

    private void RenderSample(PerformanceSample sample)
    {
        _latest = sample;
        _currentItems = BuildItems(_latest, PerformanceMonitor.MemoryPercent(_latest));
        PerfItem[] visible = _currentItems.Where(item => !IsHidden(item.Key)).ToArray();
        if (!visible.Any(item => item.Key == _selectedKey))
        {
            _selectedKey = visible.FirstOrDefault()?.Key ?? "cpu";
        }

        RenderSidebar(visible);
        RenderDetail(_currentItems.First(item => item.Key == _selectedKey));
    }

    // Before the first full sample the GPU tile is a "gpu" placeholder; hiding the real
    // adapter hides the placeholder too, so a hidden GPU does not flash in at startup.
    private bool IsHidden(string key)
    {
        List<string> hidden = State.Settings.HiddenPerformanceItems;
        return hidden.Contains(key)
            || (key == "gpu" && hidden.Any(candidate => candidate.StartsWith("gpu:", StringComparison.Ordinal)));
    }

    private void SetHidden(string key, bool hidden)
    {
        List<string> keys = State.Settings.HiddenPerformanceItems;
        if (hidden && !keys.Contains(key))
        {
            keys.Add(key);
        }
        else if (!hidden)
        {
            keys.Remove(key);
        }

        SaveHiddenItems();
    }

    private void ShowAllItems()
    {
        State.Settings.HiddenPerformanceItems.Clear();
        SaveHiddenItems();
    }

    private void SaveHiddenItems()
    {
        _ = State.Settings.TrySave();
        if (_latest is not null)
        {
            RenderSample(_latest);
        }
    }

    // Rebuilt on every open: the target tile and the hidden list change between opens.
    private void BuildSidebarMenu()
    {
        ToolStripItem[] oldItems = [.. _sidebarMenu.Items.Cast<ToolStripItem>()];
        _sidebarMenu.Items.Clear();
        foreach (ToolStripItem oldItem in oldItems)
        {
            oldItem.Dispose();
        }

        PerfItem? target = _tiles
            .Where(pair => pair.Value.RectangleToScreen(pair.Value.ClientRectangle).Contains(Cursor.Position))
            .Select(pair => _currentItems.FirstOrDefault(item => item.Key == pair.Key))
            .FirstOrDefault(item => item is not null);
        if (target is not null)
        {
            ToolStripItem hide = _sidebarMenu.Items.Add($"Hide {MenuText(target.Title)}", null, (_, _) => SetHidden(target.Key, true));
            hide.Enabled = _tiles.Count > 1; // never hide the last tile
            _sidebarMenu.Items.Add(new ToolStripSeparator());
        }

        ToolStripMenuItem showHidden = new("Show hidden items");
        foreach (PerfItem item in _currentItems.Where(item => IsHidden(item.Key)))
        {
            string key = item.Key == "gpu"
                ? State.Settings.HiddenPerformanceItems.First(candidate => candidate.StartsWith("gpu:", StringComparison.Ordinal))
                : item.Key;
            showHidden.DropDownItems.Add(MenuText(item.Title), null, (_, _) => SetHidden(key, false));
        }

        if (showHidden.DropDownItems.Count > 1)
        {
            showHidden.DropDownItems.Add(new ToolStripSeparator());
            showHidden.DropDownItems.Add("Show all", null, (_, _) => ShowAllItems());
        }

        showHidden.Enabled = showHidden.DropDownItems.Count > 0;
        _sidebarMenu.Items.Add(showHidden);
    }

    private static string MenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    private PerfItem[] BuildItems(PerformanceSample sample, double memoryPercent)
    {
        HardwareReadings readings = _sensors.Latest;
        List<PerfItem> items =
        [
            new("cpu", "CPU", $"{sample.CpuPercent:0}%  {sample.CurrentCpuSpeedGhz:0.00} GHz{TemperatureSuffix(readings.CpuTemperatureCelsius)}", sample.CpuPercent, CpuColor),
            new("memory", "Memory", $"{FormatUtil.Bytes(sample.TotalMemoryBytes - sample.AvailableMemoryBytes)} / {FormatUtil.Bytes(sample.TotalMemoryBytes)} ({memoryPercent:0}%)", memoryPercent, MemoryColor)
        ];

        items.AddRange(sample.Disks.Select(disk => new PerfItem(
            disk.Key,
            disk.Name,
            $"Active {disk.ActivePercent:0}%{TemperatureSuffix(HottestDiskCelsius(disk, readings))}  R: {FormatUtil.Bytes((long)disk.ReadBytesPerSecond)}/s  W: {FormatUtil.Bytes((long)disk.WriteBytesPerSecond)}/s",
            Math.Min(100, disk.ActivePercent),
            DiskColor)));

        items.AddRange(sample.Networks.Select(network => new PerfItem(
            network.Key,
            network.Name,
            NetworkSubtitle(network),
            network.UtilizationPercent,
            NetworkColor)));

        if (sample.Gpus.Count == 0)
        {
            items.Add(new PerfItem("gpu", "GPU 0", "not sampled yet", 0, GpuColor));
        }
        else
        {
            items.AddRange(sample.Gpus.Select(gpu => new PerfItem(
                gpu.Key,
                gpu.Name,
                $"{gpu.UtilizationPercent:0}%{TemperatureSuffix(readings.GpuTemperatureCelsius)}  {GpuMemoryText(gpu)}",
                gpu.UtilizationPercent,
                GpuColor)));
        }

        return items.ToArray();
    }

    private void RenderSidebar(IReadOnlyList<PerfItem> items)
    {
        _sidebar.SuspendLayout();
        HashSet<string> liveKeys = items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string key in _tiles.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
        {
            PerfTile oldTile = _tiles[key];
            _sidebar.Controls.Remove(oldTile);
            oldTile.Dispose();
            _tiles.Remove(key);
        }

        int width = SidebarTileWidth();
        for (int index = 0; index < items.Count; index++)
        {
            PerfItem item = items[index];
            if (!_tiles.TryGetValue(item.Key, out PerfTile? tile))
            {
                tile = new PerfTile(SelectItem);
                _tiles[item.Key] = tile;
                _sidebar.Controls.Add(tile);
            }

            tile.Update(item, GetHistory(item.Key), item.Key == _selectedKey, width, BackColor);
            if (_sidebar.Controls.GetChildIndex(tile) != index)
            {
                _sidebar.Controls.SetChildIndex(tile, index);
            }
        }

        _sidebar.ResumeLayout();
    }

    private void RenderDetail(PerfItem? item)
    {
        if (_latest is null)
        {
            return;
        }

        if (item is null)
        {
            return;
        }

        _detailTitle.Text = item.Title;
        _detailSubtitle.Text = item.Key == "cpu" ? _latest.CpuInfo.Name : item.Subtitle;

        if (item.Key == "cpu")
        {
            RenderCpuCoreGraphs(_latest);
            SetStats([
                new("Utilization", $"{_latest.CpuPercent:0}%"),
                new("Speed", $"{_latest.CurrentCpuSpeedGhz:0.00} GHz"),
                new("Base speed", $"{_latest.CpuInfo.BaseSpeedGhz:0.00} GHz"),
                new("Temperature", CpuTemperatureText(_sensors.Latest)),
                new("Virtualization", _latest.CpuInfo.VirtualizationEnabled ? "Enabled" : "Disabled"),
                new("Sockets", _latest.CpuInfo.Sockets.ToString()),
                new("Cores", _latest.CpuInfo.Cores.ToString()),
                new("Logical processors", _latest.CpuInfo.LogicalProcessors.ToString()),
                new("Processes", _latest.ProcessCount.ToString()),
                new("Threads", _latest.ThreadCount.ToString()),
                new("Handles", _latest.HandleCount.ToString()),
                new("Up time", FormatUtil.Duration(_latest.Uptime)),
                new("L1 cache", FormatUtil.Bytes(_latest.CpuInfo.L1CacheBytes)),
                new("L2 cache", FormatUtil.Bytes(_latest.CpuInfo.L2CacheBytes)),
                new("L3 cache", FormatUtil.Bytes(_latest.CpuInfo.L3CacheBytes))
            ]);
            return;
        }

        DiskPerformanceSample? diskSample = _latest.Disks.FirstOrDefault(disk => disk.Key == item.Key);
        if (diskSample?.Members is { Count: > 0 })
        {
            RenderDiskMemberGraphs(diskSample);
            SetStats(StatsFor(item));
            return;
        }

        // One graph serves every single-series item; switching items only restyles it.
        if (_detailGraph is null || !_detailHost.Controls.Contains(_detailGraph))
        {
            ClearDetailHost();
            _detailGraph = new GraphControl { Dock = DockStyle.Fill };
            _detailHost.Controls.Add(_detailGraph);
        }

        _detailGraph.Caption = item.Title;
        _detailGraph.LineColor = item.Color;
        _detailGraph.FillColor = Color.FromArgb(48, item.Color);
        _detailGraph.SetSamples(GetHistory(item.Key));
        SetStats(StatsFor(item));
    }

    // Controls.Clear() only detaches children and keeps their window handles alive, so every
    // tile switch used to leak a graph (or a whole per-core grid). Dispose what was hosted.
    private void ClearDetailHost()
    {
        while (_detailHost.Controls.Count > 0)
        {
            _detailHost.Controls[0].Dispose();
        }

        _detailGraph = null;
        _cpuGrid = null;
        _memberGrid = null;
        _diskMemberGraphs.Clear();
    }

    // One control draws every core's graph: a control per core meant dozens of separate paints
    // on every tick.
    private void RenderCpuCoreGraphs(PerformanceSample sample)
    {
        int count = Math.Max(1, sample.CpuCorePercents.Count);
        if (_cpuGrid is null || !_detailHost.Controls.Contains(_cpuGrid))
        {
            ClearDetailHost();
            _cpuGrid = new GraphGridControl
            {
                Dock = DockStyle.Fill,
                LineColor = CpuColor,
                FillColor = Color.FromArgb(48, CpuColor)
            };
            _detailHost.Controls.Add(_cpuGrid);
        }

        _cpuGrid.SetGraphs(count, CpuGridColumns(count), index => $"CPU {index}", index => GetHistory($"cpu-core:{index}"));
    }

    private void RenderDiskMemberGraphs(DiskPerformanceSample disk)
    {
        IReadOnlyList<DiskPerformanceSample> members = disk.Members ?? [];
        int count = Math.Max(1, members.Count);
        int columns = count <= 2 ? 1 : 2;
        int rows = (int)Math.Ceiling(count / (double)columns);

        bool rebuild = _memberGrid is null
            || !_detailHost.Controls.Contains(_memberGrid)
            || _diskMemberGraphs.Count != count
            || members.Any(member => !_diskMemberGraphs.ContainsKey(member.Key));
        if (rebuild)
        {
            ClearDetailHost();
            _memberGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = columns,
                RowCount = rows,
                Padding = new Padding(0)
            };

            for (int column = 0; column < columns; column++)
            {
                _memberGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            }

            for (int row = 0; row < rows; row++)
            {
                _memberGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            }

            for (int index = 0; index < members.Count; index++)
            {
                GraphControl graph = new()
                {
                    Dock = DockStyle.Fill,
                    Margin = Px(new Padding(2)),
                    LineColor = DiskColor,
                    FillColor = Color.FromArgb(48, DiskColor)
                };
                _diskMemberGraphs[members[index].Key] = graph;
                _memberGrid.Controls.Add(graph, index % columns, index / columns);
            }

            _detailHost.Controls.Add(_memberGrid);
        }

        foreach (DiskPerformanceSample member in members)
        {
            if (_diskMemberGraphs.TryGetValue(member.Key, out GraphControl? graph))
            {
                graph.Caption = $"{member.Name}   Active {member.ActivePercent:0}%   R: {FormatUtil.Bytes((long)member.ReadBytesPerSecond)}/s   W: {FormatUtil.Bytes((long)member.WriteBytesPerSecond)}/s{DiskHealthSuffix(member, _sensors.Latest)}";
                graph.SetSamples(GetHistory(member.Key));
            }
        }
    }

    private StatItem[] StatsFor(PerfItem item)
    {
        if (_latest is null)
        {
            return [];
        }

        if (item.Key == "memory")
        {
            MemoryDetails details = _latest.MemoryDetails;
            return
            [
                new("In use", FormatUtil.Bytes(_latest.TotalMemoryBytes - _latest.AvailableMemoryBytes)),
                new("Available", FormatUtil.Bytes(_latest.AvailableMemoryBytes)),
                new("Committed", $"{FormatUtil.Bytes(_latest.CommitTotalBytes)} / {FormatUtil.Bytes(_latest.CommitLimitBytes)}"),
                new("Cached", FormatUtil.Bytes(_latest.SystemCacheBytes)),
                new("Paged pool", FormatUtil.Bytes(details.PagedPoolBytes)),
                new("Non-paged pool", FormatUtil.Bytes(details.NonPagedPoolBytes)),
                new("Compressed", details.CompressedBytes is null ? "Unavailable" : FormatUtil.Bytes(details.CompressedBytes.Value)),
                new("Speed", details.SpeedMtps == 0 ? "Unknown" : $"{details.SpeedMtps} MT/s"),
                new("Slots used", details.SlotsTotal == 0 ? "Unknown" : $"{details.SlotsUsed} of {details.SlotsTotal}"),
                new("Bandwidth R/W", "Unavailable")
            ];
        }

        DiskPerformanceSample? disk = _latest.Disks.FirstOrDefault(disk => disk.Key == item.Key);
        if (disk is not null)
        {
            List<StatItem> stats =
            [
                new("Used", FormatUtil.Bytes(disk.UsedBytes)),
                new("Used %", $"{(disk.TotalBytes <= 0 ? 0 : (double)disk.UsedBytes / disk.TotalBytes * 100):0}%"),
                new("Free", FormatUtil.Bytes(disk.FreeBytes)),
                new("Capacity", FormatUtil.Bytes(disk.TotalBytes)),
                new("Active time", $"{disk.ActivePercent:0}%"),
                new("Read speed", $"{FormatUtil.Bytes((long)disk.ReadBytesPerSecond)}/s"),
                new("Write speed", $"{FormatUtil.Bytes((long)disk.WriteBytesPerSecond)}/s"),
                new("Format", disk.Kind)
            ];
            HardwareReadings readings = _sensors.Latest;
            if (readings.Disks.TryGetValue(disk.DiskIndex, out StorageHealth health))
            {
                stats.Add(new StatItem("Temperature", TemperatureText(health.TemperatureCelsius)));
                stats.Add(new StatItem("Life remaining", health.LifeRemainingPercent is { } life ? $"{life}%" : "Unavailable"));
            }

            foreach (DiskPerformanceSample member in disk.Members ?? [])
            {
                stats.Add(new StatItem(
                    member.Name,
                    $"{member.ActivePercent:0}%  R {FormatUtil.Bytes((long)member.ReadBytesPerSecond)}/s  W {FormatUtil.Bytes((long)member.WriteBytesPerSecond)}/s{DiskHealthSuffix(member, readings)}"));
            }

            return stats.ToArray();
        }

        NetworkPerformanceSample? network = _latest.Networks.FirstOrDefault(network => network.Key == item.Key);
        if (network is not null)
        {
            return
            [
                new("Send", $"{FormatUtil.Bytes((long)network.SendBytesPerSecond)}/s"),
                new("Receive", $"{FormatUtil.Bytes((long)network.ReceiveBytesPerSecond)}/s"),
                new("IPv4", string.IsNullOrWhiteSpace(network.IpAddress) ? "None" : network.IpAddress),
                new("Link speed", FormatUtil.BitsPerSecond(network.LinkSpeedBitsPerSecond)),
                new("Adapter", network.Description)
            ];
        }

        GpuPerformanceSample? gpu = _latest.Gpus.FirstOrDefault(gpu => gpu.Key == item.Key);
        if (gpu is not null)
        {
            return
            [
                new("Model", gpu.Name),
                new("Utilization", $"{gpu.UtilizationPercent:0}%"),
                new("Dedicated memory", GpuMemoryText(gpu)),
                new("Shared memory", FormatUtil.Bytes(gpu.SharedMemoryBytes)),
                new("Temperature", _sensors.Latest.TemperaturesRead ? TemperatureText(_sensors.Latest.GpuTemperatureCelsius) : ReadingText),
                new("Counters", gpu.Description)
            ];
        }

        return [new("Status", "GPU counters are not wired yet")];
    }

    private void SetStats(IReadOnlyList<StatItem> stats)
    {
        _currentStats = stats;
        _detailStats.SuspendLayout();
        while (_detailStats.Controls.Count < stats.Count)
        {
            _detailStats.Controls.Add(new StatBlock());
        }

        while (_detailStats.Controls.Count > stats.Count)
        {
            Control last = _detailStats.Controls[^1];
            _detailStats.Controls.RemoveAt(_detailStats.Controls.Count - 1);
            last.Dispose();
        }

        ReflowStats();
        _detailStats.ResumeLayout();
    }

    private void ReflowStats()
    {
        int count = Math.Min(_currentStats.Count, _detailStats.Controls.Count);
        if (count == 0)
        {
            return;
        }

        // Both scrolling panels leave room for the vertical scroll bar even while it is hidden:
        // content sized to the client width overflows once the bar appears, and the panel then
        // keeps a horizontal scroll bar.
        int available = Math.Max(1, _detailStats.Width - _detailStats.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        int columns = Math.Clamp(available / Px(104), 1, Math.Min(6, count));
        int width = Math.Max(Px(82), (available / columns) - Px(8));
        for (int index = 0; index < count; index++)
        {
            ((StatBlock)_detailStats.Controls[index]).Update(_currentStats[index], width);
        }

        // Grow the row until every line of stats shows, taking space from the graph above, but
        // never more than half the pane; past that the stats scroll.
        Control first = _detailStats.Controls[0];
        int rows = (count + columns - 1) / columns;
        int needed = _detailStats.Padding.Vertical + (rows * (first.Height + first.Margin.Vertical));
        int limit = Math.Max(Px(150), (_detailStats.Parent?.ClientSize.Height ?? 0) / 2);
        _detailStatsRow.Height = Math.Clamp(needed, Px(150), limit);
    }

    private IReadOnlyList<double> GetHistory(string key) => _monitor.History(key);

    // Leaves room for the vertical scroll bar; see ReflowStats.
    private int SidebarTileWidth() =>
        Math.Max(Px(120), _sidebar.Width - _sidebar.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - Px(2));

    private int CpuGridColumns(int count)
    {
        int maxColumns = Math.Min(8, count);
        int bestColumns = 1;
        int bestScore = int.MaxValue;
        for (int columns = 1; columns <= maxColumns; columns++)
        {
            if (count % columns != 0)
            {
                continue;
            }

            int rows = count / columns;
            if (columns < rows)
            {
                continue;
            }

            int score = columns - rows;
            if (score < bestScore)
            {
                bestScore = score;
                bestColumns = columns;
            }
        }

        return bestScore == int.MaxValue ? Math.Min(maxColumns, (int)Math.Ceiling(Math.Sqrt(count))) : bestColumns;
    }

    private static string NetworkSubtitle(NetworkPerformanceSample network)
    {
        string ip = string.IsNullOrWhiteSpace(network.IpAddress) ? "No IPv4" : network.IpAddress;
        return $"{ip}  {FormatUtil.BitsPerSecond(network.LinkSpeedBitsPerSecond)}";
    }

    private static string TemperatureText(double? celsius) => celsius is { } value ? $"{value:0} °C" : "Unavailable";

    // Tiles are narrow and clip their second line, so callers put this right after the main
    // figure. The no-break space keeps the value and unit together when the text wraps.
    private static string TemperatureSuffix(double? celsius) => celsius is { } value ? $"  {value:0}\u00A0°C" : "";

    // ACPI thermal zones are not the CPU's own sensor, so the source is labeled.
    private static string CpuTemperatureText(HardwareReadings readings) => !readings.TemperaturesRead
        ? ReadingText
        : readings.CpuTemperatureCelsius is { } value ? $"{value:0} °C (ACPI)" : "Unavailable";

    private static double? HottestDiskCelsius(DiskPerformanceSample disk, HardwareReadings readings)
    {
        double? hottest = readings.Disks.GetValueOrDefault(disk.DiskIndex).TemperatureCelsius;
        foreach (DiskPerformanceSample member in disk.Members ?? [])
        {
            if (readings.Disks.GetValueOrDefault(member.DiskIndex).TemperatureCelsius is { } value)
            {
                hottest = Math.Max(hottest ?? value, value);
            }
        }

        return hottest;
    }

    private static string DiskHealthSuffix(DiskPerformanceSample disk, HardwareReadings readings)
    {
        if (!readings.Disks.TryGetValue(disk.DiskIndex, out StorageHealth health))
        {
            return "";
        }

        string life = health.LifeRemainingPercent is { } percent ? $"  {percent}% life" : "";
        return TemperatureSuffix(health.TemperatureCelsius) + life;
    }

    private static string GpuMemoryText(GpuPerformanceSample gpu) =>
        gpu.DedicatedMemoryTotalBytes > 0
            ? $"{FormatUtil.Bytes(gpu.DedicatedMemoryBytes)} / {FormatUtil.Bytes(gpu.DedicatedMemoryTotalBytes)}"
            : FormatUtil.Bytes(gpu.DedicatedMemoryBytes);

    private void ResizeSidebarTiles()
    {
        int width = SidebarTileWidth();
        foreach (PerfTile tile in _tiles.Values)
        {
            tile.ResizeTo(width);
        }
    }

    private void SelectItem(string key)
    {
        if (_latest is null)
        {
            return;
        }

        PerfItem? item = _currentItems.FirstOrDefault(candidate => candidate.Key == key);
        if (item is null)
        {
            return;
        }

        _selectedKey = key;
        RenderSidebar(_currentItems);
        RenderDetail(item);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.Sampled -= OnSampled;
            _sensors.Updated -= OnSensorsUpdated;
            _sidebarMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class PerfTile : UserControl
    {
        // Theme-aware so the selected tile keeps readable text in dark mode.
        private static readonly Color LightSelectedBackColor = Color.FromArgb(204, 228, 247);
        private static readonly Color LightHoverBackColor = Color.FromArgb(226, 238, 252);
        private static readonly Color DarkSelectedBackColor = Color.FromArgb(40, 78, 122);
        private static readonly Color DarkHoverBackColor = Color.FromArgb(56, 64, 76);
        private readonly Action<string> _select;
        private readonly GraphControl _graph = new() { Compact = true };
        private readonly Label _title = new();
        private readonly Label _subtitle = new();
        private readonly Font _titleFont;
        private readonly int _titleHeight;
        private readonly int _subtitleHeight;
        private PerfItem? _item;
        private Color _pageBackColor;
        private bool _selected;
        private bool _hovered;

        public PerfTile(Action<string> select)
        {
            _select = select;
            DoubleBuffered = true;
            Margin = Px(new Padding(0, 0, 0, 8));
            Cursor = Cursors.Hand;

            _title.AutoSize = false;
            _title.AutoEllipsis = true;
            _titleFont = new Font(Font, FontStyle.Bold);
            _title.Font = _titleFont;
            _subtitle.AutoSize = false;
            _subtitle.AutoEllipsis = true;
            // The subtitle wraps to two lines; both must fit or the label drops the second one.
            _titleHeight = Math.Max(Px(22), TextHeight(_titleFont));
            _subtitleHeight = Math.Max(Px(32), TextHeight(Font, 2));
            Height = Math.Max(Px(66), Px(7) + _titleHeight + _subtitleHeight + Px(4));

            Controls.Add(_graph);
            Controls.Add(_title);
            Controls.Add(_subtitle);
            WireClick(this);
        }

        public void Update(PerfItem item, IReadOnlyList<double> samples, bool selected, int width, Color pageBackColor)
        {
            _item = item;
            _selected = selected;
            _pageBackColor = pageBackColor;
            Width = width;
            _graph.LineColor = item.Color;
            _graph.FillColor = Color.FromArgb(48, item.Color);
            _graph.SetSamples(samples);
            _title.Text = item.Title;
            _subtitle.Text = item.Subtitle;
            ApplyBackColor();
            ResizeTo(width);
        }

        public void ResizeTo(int width)
        {
            Width = width;
            int graphWidth = Math.Clamp(width / 4, Px(42), Px(58));
            _graph.SetBounds(Px(4), Px(8), graphWidth, Px(48));
            int textLeft = graphWidth + Px(14);
            int textWidth = Math.Max(Px(20), width - textLeft - Px(4));
            _title.SetBounds(textLeft, Px(7), textWidth, _titleHeight);
            _subtitle.SetBounds(textLeft, _title.Bottom, textWidth, _subtitleHeight);
        }

        private void WireClick(Control control)
        {
            control.Click += (_, _) =>
            {
                if (_item is not null)
                {
                    _select(_item.Key);
                }
            };
            control.MouseEnter += (_, _) =>
            {
                _hovered = true;
                ApplyBackColor();
            };
            control.MouseLeave += (_, _) =>
            {
                _hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
                ApplyBackColor();
            };

            foreach (Control child in control.Controls)
            {
                WireClick(child);
            }
        }

        private void ApplyBackColor()
        {
            bool dark = _pageBackColor.GetBrightness() < 0.5f;
            BackColor = _selected ? (dark ? DarkSelectedBackColor : LightSelectedBackColor)
                : _hovered ? (dark ? DarkHoverBackColor : LightHoverBackColor)
                : _pageBackColor;
            _title.BackColor = BackColor;
            _subtitle.BackColor = BackColor;
            _graph.BackColor = BackColor;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _titleFont.Dispose();
            }
        }
    }

    private sealed record PerfItem(string Key, string Title, string Subtitle, double Value, Color Color);

    private sealed record StatItem(string Label, string Value);

    private sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
    {
        public BufferedFlowLayoutPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    private sealed class StatBlock : UserControl
    {
        private readonly Label _label = new();
        private readonly Label _value = new();
        private readonly Font _valueFont;

        public StatBlock()
        {
            Margin = Px(new Padding(0, 0, 8, 2));

            _label.AutoSize = false;
            _label.AutoEllipsis = true;
            _label.ForeColor = Color.DimGray;
            _label.Location = new Point(0, 0);
            _label.Height = TextHeight(Font);

            _value.AutoSize = false;
            _value.AutoEllipsis = true;
            _valueFont = RelativeFont(Font, 10);
            _value.Font = _valueFont;
            _value.Location = new Point(0, _label.Height);
            _value.Height = TextHeight(_valueFont);
            Height = Math.Max(Px(38), _value.Bottom + Px(2));

            Controls.Add(_label);
            Controls.Add(_value);
        }

        public void Update(StatItem item, int width)
        {
            Width = width;
            _label.Width = width;
            _value.Width = width;
            if (_label.Text != item.Label)
            {
                _label.Text = item.Label;
            }

            if (_value.Text != item.Value)
            {
                _value.Text = item.Value;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _valueFont.Dispose();
            }
        }
    }
}
