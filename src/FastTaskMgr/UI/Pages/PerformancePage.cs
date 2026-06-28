using FastTaskMgr.App;
using FastTaskMgr.Core.Performance;
using FastTaskMgr.UI.Controls;

namespace FastTaskMgr.UI.Pages;

internal sealed class PerformancePage : PageBase
{
    private static readonly Color CpuColor = Color.FromArgb(0, 120, 170);
    private static readonly Color MemoryColor = Color.FromArgb(125, 70, 170);
    private static readonly Color DiskColor = Color.FromArgb(80, 140, 45);
    private static readonly Color NetworkColor = Color.FromArgb(200, 40, 105);
    private static readonly Color GpuColor = Color.FromArgb(120, 65, 220);

    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly BufferedFlowLayoutPanel _sidebar = new();
    private readonly Panel _detailHost = new();
    private readonly Label _detailTitle = new();
    private readonly Label _detailSubtitle = new();
    private readonly BufferedFlowLayoutPanel _detailStats = new();
    private readonly Dictionary<string, List<double>> _history = [];
    private readonly Dictionary<string, PerfTile> _tiles = [];
    private readonly Dictionary<int, GraphControl> _cpuCoreGraphs = [];
    private IReadOnlyList<PerfItem> _currentItems = [];
    private IReadOnlyList<StatItem> _currentStats = [];
    private string? _renderedDetailKey;
    private GraphControl? _detailGraph;
    private TableLayoutPanel? _cpuGrid;
    private int _cpuGridColumns;
    private string _selectedKey = "cpu";
    private PerformanceSample? _latest;
    private bool _refreshing;

    public PerformancePage(AppState state)
        : base(state)
    {
        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 6,
            IsSplitterFixed = false
        };
        split.SizeChanged += (_, _) => ConfigureSplitter(split);
        Controls.Add(split);

        _sidebar.Dock = DockStyle.Fill;
        _sidebar.FlowDirection = FlowDirection.TopDown;
        _sidebar.WrapContents = false;
        _sidebar.AutoScroll = true;
        _sidebar.Padding = new Padding(6);
        _sidebar.Resize += (_, _) => ResizeSidebarTiles();
        split.Panel1.Controls.Add(_sidebar);

        TableLayoutPanel detail = new()
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(8, 6, 8, 6)
        };
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        split.Panel2.Controls.Add(detail);

        Panel header = new() { Dock = DockStyle.Fill };
        _detailTitle.Font = new Font(Font.FontFamily, 20, FontStyle.Regular);
        _detailTitle.SetBounds(0, 0, 300, 34);
        _detailTitle.AutoSize = false;
        _detailTitle.AutoEllipsis = true;
        _detailSubtitle.SetBounds(2, 36, 300, 22);
        _detailSubtitle.AutoSize = false;
        _detailSubtitle.AutoEllipsis = true;
        header.Resize += (_, _) =>
        {
            _detailTitle.Width = header.ClientSize.Width;
            _detailSubtitle.Width = Math.Max(1, header.ClientSize.Width - 2);
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
        _detailStats.Padding = new Padding(0, 6, 0, 0);
        _detailStats.WrapContents = true;
        _detailStats.AutoScroll = true;
        _detailStats.Resize += (_, _) => ReflowStats();
        detail.Controls.Add(_detailStats, 0, 2);

        _timer.Tick += async (_, _) => await RefreshSampleAsync();
        State.SettingsChanged += (_, _) => ConfigureTimer();
    }

    public override string Title => "Performance";
    public override bool UsesSearch => false;

    public override async void OnShow()
    {
        ConfigureTimer();
        await RefreshSampleAsync();
    }

    public override void OnHide() => _timer.Stop();

    private void ConfigureTimer()
    {
        int interval = State.Settings.UpdateSpeed.ToMilliseconds();
        _timer.Stop();
        if (interval > 0)
        {
            _timer.Interval = interval;
            _timer.Start();
        }
    }

    private static void ConfigureSplitter(SplitContainer split)
    {
        if (split.Width < 260)
        {
            return;
        }

        int target = Math.Clamp(split.SplitterDistance, 170, Math.Min(260, split.Width - 220));
        if (split.SplitterDistance != target)
        {
            split.SplitterDistance = target;
        }
    }

    private async Task RefreshSampleAsync()
    {
        if (_refreshing || IsDisposed || (ModifierKeys & Keys.Control) == Keys.Control)
        {
            return;
        }

        _refreshing = true;
        try
        {
            PerformanceSample sample = await Task.Run(() => State.Performance.Sample());
            if (!IsDisposed)
            {
                RenderSample(sample);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RenderSample(PerformanceSample sample)
    {
        _latest = sample;
        AddHistory("cpu", _latest.CpuPercent);
        for (int index = 0; index < _latest.CpuCorePercents.Count; index++)
        {
            AddHistory($"cpu-core:{index}", _latest.CpuCorePercents[index]);
        }

        double memoryPercent = _latest.TotalMemoryBytes == 0
            ? 0
            : (double)(_latest.TotalMemoryBytes - _latest.AvailableMemoryBytes) / _latest.TotalMemoryBytes * 100;
        AddHistory("memory", memoryPercent);

        foreach (DiskPerformanceSample disk in _latest.Disks)
        {
            AddHistory(disk.Key, Math.Min(100, disk.ActivePercent));
        }

        foreach (NetworkPerformanceSample network in _latest.Networks)
        {
            AddHistory(network.Key, network.UtilizationPercent);
        }

        foreach (GpuPerformanceSample gpu in _latest.Gpus)
        {
            AddHistory(gpu.Key, gpu.UtilizationPercent);
        }

        _currentItems = BuildItems(_latest, memoryPercent);
        if (!_currentItems.Any(item => item.Key == _selectedKey))
        {
            _selectedKey = "cpu";
        }

        RenderSidebar(_currentItems);
        RenderDetail(_currentItems.First(item => item.Key == _selectedKey));
    }

    private PerfItem[] BuildItems(PerformanceSample sample, double memoryPercent)
    {
        List<PerfItem> items =
        [
            new("cpu", "CPU", $"{sample.CpuPercent:0}%  {sample.CurrentCpuSpeedGhz:0.00} GHz", sample.CpuPercent, CpuColor),
            new("memory", "Memory", $"{FormatUtil.Bytes(sample.TotalMemoryBytes - sample.AvailableMemoryBytes)} / {FormatUtil.Bytes(sample.TotalMemoryBytes)} ({memoryPercent:0}%)", memoryPercent, MemoryColor)
        ];

        items.AddRange(sample.Disks.Select(disk => new PerfItem(
            disk.Key,
            disk.Name,
            $"Active {disk.ActivePercent:0}%  R: {FormatUtil.Bytes((long)disk.ReadBytesPerSecond)}/s  W: {FormatUtil.Bytes((long)disk.WriteBytesPerSecond)}/s",
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
                $"{gpu.UtilizationPercent:0}%  Dedicated {FormatUtil.Bytes(gpu.DedicatedMemoryBytes)}",
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
            _renderedDetailKey = item.Key;
            return;
        }

        if (_renderedDetailKey != item.Key || _detailGraph is null || !_detailHost.Controls.Contains(_detailGraph))
        {
            _detailHost.Controls.Clear();
            _cpuCoreGraphs.Clear();
            _cpuGrid = null;
            _detailGraph = new GraphControl { Dock = DockStyle.Fill };
            _detailHost.Controls.Add(_detailGraph);
        }

        _detailGraph.Caption = item.Title;
        _detailGraph.LineColor = item.Color;
        _detailGraph.FillColor = Color.FromArgb(48, item.Color);
        _detailGraph.SetSamples(GetHistory(item.Key));
        SetStats(StatsFor(item));
        _renderedDetailKey = item.Key;
    }

    private void RenderCpuCoreGraphs(PerformanceSample sample)
    {
        int count = Math.Max(1, sample.CpuCorePercents.Count);
        int columns = CpuGridColumns(count);
        int rows = (int)Math.Ceiling(count / (double)columns);

        if (_renderedDetailKey != "cpu" || _cpuGrid is null || _cpuCoreGraphs.Count != count || _cpuGridColumns != columns)
        {
            _detailHost.Controls.Clear();
            _detailGraph = null;
            _cpuCoreGraphs.Clear();
            _cpuGridColumns = columns;
            _cpuGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = columns,
                RowCount = rows,
                Padding = new Padding(0)
            };

            for (int column = 0; column < columns; column++)
            {
                _cpuGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            }

            for (int row = 0; row < rows; row++)
            {
                _cpuGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            }

            for (int index = 0; index < count; index++)
            {
                GraphControl graph = new()
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(2),
                    Caption = $"CPU {index}",
                    LineColor = CpuColor,
                    FillColor = Color.FromArgb(48, CpuColor)
                };
                _cpuCoreGraphs[index] = graph;
                _cpuGrid.Controls.Add(graph, index % columns, index / columns);
            }

            _detailHost.Controls.Add(_cpuGrid);
        }

        for (int index = 0; index < count; index++)
        {
            _cpuCoreGraphs[index].SetSamples(GetHistory($"cpu-core:{index}"));
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
            return
            [
                new("In use", FormatUtil.Bytes(_latest.TotalMemoryBytes - _latest.AvailableMemoryBytes)),
                new("Available", FormatUtil.Bytes(_latest.AvailableMemoryBytes)),
                new("Committed", $"{FormatUtil.Bytes(_latest.CommitTotalBytes)} / {FormatUtil.Bytes(_latest.CommitLimitBytes)}"),
                new("Cached", FormatUtil.Bytes(_latest.SystemCacheBytes))
            ];
        }

        DiskPerformanceSample? disk = _latest.Disks.FirstOrDefault(disk => disk.Key == item.Key);
        if (disk is not null)
        {
            return
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
                new("Utilization", $"{gpu.UtilizationPercent:0}%"),
                new("Dedicated memory", FormatUtil.Bytes(gpu.DedicatedMemoryBytes)),
                new("Shared memory", FormatUtil.Bytes(gpu.SharedMemoryBytes)),
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

        int width = StatBlockWidth(stats.Count);
        for (int index = 0; index < stats.Count; index++)
        {
            ((StatBlock)_detailStats.Controls[index]).Update(stats[index], width);
        }

        _detailStats.ResumeLayout();
    }

    private void ReflowStats()
    {
        if (_currentStats.Count == 0 || _detailStats.Controls.Count == 0)
        {
            return;
        }

        int width = StatBlockWidth(_currentStats.Count);
        for (int index = 0; index < _detailStats.Controls.Count && index < _currentStats.Count; index++)
        {
            ((StatBlock)_detailStats.Controls[index]).Update(_currentStats[index], width);
        }
    }

    private void AddHistory(string key, double value)
    {
        if (!_history.TryGetValue(key, out List<double>? samples))
        {
            samples = [];
            _history[key] = samples;
        }

        samples.Add(Math.Clamp(value, 0, 100));
        if (samples.Count > 120)
        {
            samples.RemoveAt(0);
        }
    }

    private IReadOnlyList<double> GetHistory(string key) => _history.TryGetValue(key, out List<double>? values) ? values : [];

    private int SidebarTileWidth()
    {
        int scroll = _sidebar.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        return Math.Max(120, _sidebar.ClientSize.Width - _sidebar.Padding.Horizontal - scroll - 2);
    }

    private int StatBlockWidth(int count)
    {
        int available = Math.Max(1, _detailStats.ClientSize.Width - _detailStats.Padding.Horizontal);
        int columns = Math.Clamp(available / 104, 1, Math.Min(6, Math.Max(1, count)));
        return Math.Max(82, (available / columns) - 8);
    }

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

    private sealed class PerfTile : UserControl
    {
        private readonly Action<string> _select;
        private readonly GraphControl _graph = new() { Compact = true };
        private readonly Label _title = new();
        private readonly Label _subtitle = new();
        private PerfItem? _item;

        public PerfTile(Action<string> select)
        {
            _select = select;
            DoubleBuffered = true;
            Height = 66;
            Margin = new Padding(0, 0, 0, 8);
            Cursor = Cursors.Hand;

            _title.AutoSize = false;
            _title.AutoEllipsis = true;
            _title.Font = new Font(Font, FontStyle.Bold);
            _subtitle.AutoSize = false;
            _subtitle.AutoEllipsis = true;

            Controls.Add(_graph);
            Controls.Add(_title);
            Controls.Add(_subtitle);
            WireClick(this);
        }

        public void Update(PerfItem item, IReadOnlyList<double> samples, bool selected, int width, Color pageBackColor)
        {
            _item = item;
            Width = width;
            BackColor = selected ? Color.FromArgb(235, 235, 235) : pageBackColor;
            _title.BackColor = BackColor;
            _subtitle.BackColor = BackColor;
            _graph.BackColor = BackColor;
            _graph.LineColor = item.Color;
            _graph.FillColor = Color.FromArgb(48, item.Color);
            _graph.SetSamples(samples);
            _title.Text = item.Title;
            _subtitle.Text = item.Subtitle;
            ResizeTo(width);
        }

        public void ResizeTo(int width)
        {
            Width = width;
            int graphWidth = Math.Clamp(width / 4, 42, 58);
            _graph.SetBounds(4, 8, graphWidth, 48);
            int textLeft = graphWidth + 14;
            int textWidth = Math.Max(20, width - textLeft - 4);
            _title.SetBounds(textLeft, 7, textWidth, 22);
            _subtitle.SetBounds(textLeft, 29, textWidth, 32);
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

            foreach (Control child in control.Controls)
            {
                WireClick(child);
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

        public StatBlock()
        {
            Height = 38;
            Margin = new Padding(0, 0, 8, 2);

            _label.AutoSize = false;
            _label.ForeColor = Color.DimGray;
            _label.Location = new Point(0, 0);
            _label.Height = 14;

            _value.AutoSize = false;
            _value.AutoEllipsis = true;
            _value.Location = new Point(0, 15);
            _value.Height = 21;
            _value.Font = new Font(Font.FontFamily, 10f, FontStyle.Regular);

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
    }
}
