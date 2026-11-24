using System.Runtime.InteropServices;

namespace FastTaskMgr.UI.Controls;

internal sealed class VirtualTable<T> : ListView
    where T : class
{
    private static readonly Color LightHoverBackColor = Color.FromArgb(226, 238, 252);
    private static readonly Color DarkHoverBackColor = Color.FromArgb(56, 64, 76);
    private readonly IReadOnlyList<TableColumn<T>> _columns;
    private readonly Func<T, object?> _keySelector;
    private IReadOnlyList<T> _rows = [];
    // Rows are formatted once and reused by painting, change detection and the hit tests that
    // raise RetrieveVirtualItem. Items are cached until the next SetRows.
    private ListViewItem?[] _itemCache = [];
    private readonly int[] _paintColumnWidths;
    private IntPtr _paintFont;
    private int _paintLineHeight;
    private int _textLeftMargin;
    private int _textRightMargin;
    private int _sortColumn;
    private bool _sortDescending;
    private int _hotIndex = -1;
    private int _userHorizontalScroll;

    public VirtualTable(IReadOnlyList<TableColumn<T>> columns, Func<T, object?>? keySelector = null)
    {
        _columns = columns;
        _keySelector = keySelector ?? (row => row);
        Dock = DockStyle.Fill;
        FullRowSelect = true;
        GridLines = true;
        HideSelection = false;
        MultiSelect = true;
        UseCompatibleStateImageBehavior = false;
        View = View.Details;
        VirtualMode = true;
        DoubleBuffered = true;
        _paintColumnWidths = new int[columns.Count];

        foreach (TableColumn<T> column in columns)
        {
            Columns.Add(column.Title, UiScale.Px(column.Width));
        }

        UpdateColumnHeaders();
    }

    public T? SelectedRow => SelectedIndices.Count == 0 || SelectedIndices[0] >= _rows.Count
        ? null
        : _rows[SelectedIndices[0]];

    public IReadOnlyList<T> SelectedRows => SelectedIndices
        .Cast<int>()
        .Where(index => index >= 0 && index < _rows.Count)
        .Select(index => _rows[index])
        .ToArray();

    public string SortColumnTitle => _columns[_sortColumn].Title;
    public bool SortDescending => _sortDescending;
    public event EventHandler? SortChanged;

    public void SetSort(string columnTitle, bool descending)
    {
        for (int index = 0; index < _columns.Count; index++)
        {
            if (!_columns[index].Title.Equals(columnTitle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _sortColumn = index;
            _sortDescending = descending;
            UpdateColumnHeaders();
            return;
        }
    }

    // The native list view occasionally scrolls itself while the mouse rests over it
    // (hot-item updates during content churn), so the current scroll position cannot be
    // trusted as the user's intent. Track intent from real input and re-assert it on refresh.
    protected override void WndProc(ref Message m)
    {
        const int lvmSetItemCount = 0x102F;
        const int lvsicfNoScroll = 0x2;
        const int wmReflectNotify = 0x204E;
        if (m.Msg == wmReflectNotify && ListViewNative.IsCustomDraw(m.LParam))
        {
            m.Result = CustomDraw(m.LParam);
            return;
        }

        if (m.Msg == lvmSetItemCount)
        {
            // WinForms sends this without LVSICF_NOSCROLL, and on count changes the native
            // control then scrolls the focused item into view, flashing the viewport back
            // to column 0. Adding the flag keeps the scroll position untouched.
            m.LParam = (IntPtr)((long)m.LParam | lvsicfNoScroll);
        }

        base.WndProc(ref m);
        const int wmKeyDown = 0x0100;
        const int wmHScroll = 0x0114;
        const int wmMouseHWheel = 0x020E;
        if (m.Msg is wmHScroll or wmMouseHWheel or wmKeyDown)
        {
            _userHorizontalScroll = HorizontalScrollPosition();
        }
    }

    public void SetRows(IEnumerable<T> rows)
    {
        int horizontalScroll = _userHorizontalScroll;
        int previousCount = _rows.Count;
        ListViewItem?[] previousItems = _itemCache;
        // Freeze redraw natively rather than with BeginUpdate/EndUpdate: WinForms' EndUpdate
        // invalidates the whole control, which forced a full repaint on every refresh.
        SetRedraw(false);
        try
        {
            int[] selectedBefore = SelectedIndices
                .Cast<int>()
                .Where(index => index >= 0 && index < _rows.Count)
                .ToArray();
            HashSet<object> selectedKeys = selectedBefore
                .Select(index => _keySelector(_rows[index]))
                .Where(key => key is not null)
                .Select(key => key!)
                .ToHashSet();

            TableColumn<T> column = _columns[_sortColumn];
            Func<T, IComparable?> key = column.SortKey ?? (row => column.Text(row));
            IOrderedEnumerable<T> sorted = _sortDescending
                ? rows.OrderByDescending(key, Comparer<IComparable?>.Create(CompareNullable))
                : rows.OrderBy(key, Comparer<IComparable?>.Create(CompareNullable));

            _rows = sorted.ToArray();
            _itemCache = new ListViewItem?[_rows.Count];
            VirtualListSize = _rows.Count;

            List<int> selectedAfter = [];
            if (selectedKeys.Count > 0)
            {
                for (int index = 0; index < _rows.Count; index++)
                {
                    object? rowKey = _keySelector(_rows[index]);
                    if (rowKey is not null && selectedKeys.Contains(rowKey))
                    {
                        selectedAfter.Add(index);
                    }
                }
            }

            // Touch the native selection only when it moved; each change invalidates rows.
            if (!selectedBefore.SequenceEqual(selectedAfter))
            {
                SelectedIndices.Clear();
                foreach (int index in selectedAfter)
                {
                    SelectedIndices.Add(index);
                }
            }
        }
        finally
        {
            // Redraw must be re-enabled first: that runs the list view's deferred "scroll
            // focused item into view" (triggered by item-count changes while a row is
            // selected), which would otherwise undo the restore.
            SetRedraw(true);
            RestoreHorizontalScroll(horizontalScroll);
        }

        InvalidateChangedRows(previousItems, previousCount);
    }

    private void SetRedraw(bool enabled)
    {
        if (IsHandleCreated)
        {
            const int wmSetRedraw = 0x000B;
            _ = ListViewNative.SendMessage(Handle, wmSetRedraw, (IntPtr)(enabled ? 1 : 0), IntPtr.Zero);
        }
    }

    // Repaint only the visible rows whose text changed. Between ticks most rows are identical,
    // and repainting them was the bulk of the UI thread's work.
    private void InvalidateChangedRows(ListViewItem?[] previousItems, int previousCount)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        if (_rows.Count != previousCount)
        {
            Invalidate();
            return;
        }

        int top = Math.Max(0, (int)ListViewNative.SendMessage(Handle, ListViewNative.LvmGetTopIndex, IntPtr.Zero, IntPtr.Zero));
        int perPage = (int)ListViewNative.SendMessage(Handle, ListViewNative.LvmGetCountPerPage, IntPtr.Zero, IntPtr.Zero);
        int end = Math.Min(_rows.Count, top + perPage + 2);

        // The list view repaints every row inside the bounding box of its update region, so
        // scattered changes would drag in every row between them. Paint each run of changed
        // rows on its own, after flushing whatever the control invalidated itself.
        Update();
        int runStart = -1;
        for (int index = top; index <= end; index++)
        {
            bool changed = index < end
                && (index >= previousItems.Length || previousItems[index] is not { } before || !SameText(before, ItemAt(index)));
            if (changed)
            {
                if (runStart < 0)
                {
                    runStart = index;
                }

                continue;
            }

            if (runStart >= 0)
            {
                Invalidate(Rectangle.Union(GetItemRect(runStart), GetItemRect(index - 1)));
                Update();
                runStart = -1;
            }
        }
    }

    private static bool SameText(ListViewItem left, ListViewItem right)
    {
        int count = left.SubItems.Count;
        if (count != right.SubItems.Count)
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            if (!string.Equals(left.SubItems[index].Text, right.SubItems[index].Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public bool SelectKey(object key)
    {
        for (int index = 0; index < _rows.Count; index++)
        {
            if (key.Equals(_keySelector(_rows[index])))
            {
                SelectedIndices.Clear();
                SelectedIndices.Add(index);
                EnsureVisible(index);
                _userHorizontalScroll = HorizontalScrollPosition();
                return true;
            }
        }

        return false;
    }

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        int itemIndex = e.ItemIndex;
        if ((uint)itemIndex >= (uint)_rows.Count)
        {
            // Stale request from the native control while the row count changes.
            e.Item = new ListViewItem(new string[_columns.Count]);
            return;
        }

        e.Item = ItemAt(itemIndex);
    }

    private ListViewItem ItemAt(int itemIndex)
    {
        ListViewItem? item = _itemCache[itemIndex];
        if (item is null)
        {
            T row = _rows[itemIndex];
            string[] subItems = new string[_columns.Count];
            for (int index = 0; index < _columns.Count; index++)
            {
                subItems[index] = _columns[index].Text(row);
            }

            item = new ListViewItem(subItems);
            _itemCache[itemIndex] = item;
        }

        return item;
    }

    // Rows are painted with GDI straight onto the list view's DC from its custom-draw
    // notification. WinForms' owner-draw path built a Graphics object and fetched the item
    // several times for every cell, which cost more than the drawing itself.
    private IntPtr CustomDraw(IntPtr notification)
    {
        ListViewNative.NMCUSTOMDRAW draw = Marshal.PtrToStructure<ListViewNative.NMCUSTOMDRAW>(notification);
        switch (draw.DrawStage)
        {
            case ListViewNative.CddsPrePaint:
                PreparePaint(draw.Hdc);
                return ListViewNative.CdrfNotifyItemDraw;
            case ListViewNative.CddsItemPrePaint:
                DrawRow(draw.Hdc, (int)draw.ItemSpec);
                return ListViewNative.CdrfSkipDefault;
            default:
                return ListViewNative.CdrfDoDefault;
        }
    }

    private void PreparePaint(IntPtr hdc)
    {
        for (int index = 0; index < _paintColumnWidths.Length; index++)
        {
            _paintColumnWidths[index] = (int)ListViewNative.SendMessage(Handle, ListViewNative.LvmGetColumnWidth, index, 0);
        }

        IntPtr font = ListViewNative.SendMessage(Handle, ListViewNative.WmGetFont, 0, 0);
        if (font == _paintFont && _paintLineHeight > 0)
        {
            return;
        }

        // The line height and padding TextRenderer derives from the font, so text lands where
        // the TextRenderer-based cells put it.
        _paintFont = font;
        IntPtr previous = ListViewNative.SelectObject(hdc, font);
        _ = ListViewNative.GetTextMetrics(hdc, out ListViewNative.TEXTMETRIC metrics);
        _ = ListViewNative.SelectObject(hdc, previous);
        _paintLineHeight = metrics.Height;
        float overhang = metrics.Height / 6f;
        _textLeftMargin = (int)Math.Ceiling(overhang);
        _textRightMargin = (int)Math.Ceiling(overhang * 1.5f);
    }

    private void DrawRow(IntPtr hdc, int index)
    {
        ListViewNative.RECT row = new() { Left = ListViewNative.LvirBounds };
        if (ListViewNative.SendMessage(Handle, ListViewNative.LvmGetItemRect, index, ref row) == 0)
        {
            return;
        }

        bool selected = (ListViewNative.SendMessage(Handle, ListViewNative.LvmGetItemState, index, ListViewNative.LvisSelected) & ListViewNative.LvisSelected) != 0;
        Color back = selected ? SystemColors.Highlight : index == _hotIndex ? HoverBackColor() : BackColor;
        _ = ListViewNative.SetDCBrushColor(hdc, ColorTranslator.ToWin32(back));
        _ = ListViewNative.FillRect(hdc, ref row, ListViewNative.DcBrush);
        if ((uint)index >= (uint)_rows.Count)
        {
            return; // stale request from the native control while the row count changes
        }

        ListViewItem item = ItemAt(index);
        IntPtr previousFont = ListViewNative.SelectObject(hdc, _paintFont);
        int previousColor = ListViewNative.SetTextColor(hdc, ColorTranslator.ToWin32(selected ? SystemColors.HighlightText : ForeColor));
        int previousMode = ListViewNative.SetBkMode(hdc, ListViewNative.BkModeTransparent);
        int clientWidth = ClientSize.Width;
        int left = row.Left;
        for (int column = 0; column < _paintColumnWidths.Length && left < clientWidth; column++)
        {
            int width = _paintColumnWidths[column];
            if (left + width > 0)
            {
                DrawCell(hdc, item.SubItems[column].Text, left, row.Top, width, row.Bottom - row.Top);
            }

            left += width;
        }

        _ = ListViewNative.SetBkMode(hdc, previousMode);
        _ = ListViewNative.SetTextColor(hdc, previousColor);
        _ = ListViewNative.SelectObject(hdc, previousFont);
        // No manual grid lines: the native GridLines pass draws over the rows afterwards.
    }

    // Matches the TextRenderer call cells used before (Left | VerticalCenter | EndEllipsis |
    // NoPrefix inside the cell inset by 4px), including how TextRenderer centers a line itself
    // instead of passing DT_SINGLELINE.
    private void DrawCell(IntPtr hdc, string text, int left, int top, int width, int height)
    {
        if (text.Length == 0)
        {
            return;
        }

        int inset = UiScale.Px(4);
        ListViewNative.RECT bounds = new() { Left = left + inset, Top = top, Right = left + inset + Math.Max(0, width - 2 * inset), Bottom = top + height };
        if (_paintLineHeight <= height && text.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            bounds.Top = top + height / 2 - _paintLineHeight / 2;
            bounds.Bottom = bounds.Top + height;
        }

        // Most cells are short ASCII that fits. ExtTextOut puts those pixels exactly where
        // DrawTextEx would at a fraction of the cost, since DrawTextEx lays each line out again.
        int available = bounds.Right - bounds.Left - _textLeftMargin - _textRightMargin;
        if (!text.AsSpan().ContainsAnyExceptInRange(' ', '~')
            && ListViewNative.GetTextExtentPoint32(hdc, text, text.Length, out ListViewNative.SIZE size)
            && size.Width <= available)
        {
            _ = ListViewNative.ExtTextOut(hdc, bounds.Left + _textLeftMargin, bounds.Top, 0, IntPtr.Zero, text, text.Length, IntPtr.Zero);
            return;
        }

        // Ellipsis, line breaks and other scripts: let DrawTextEx lay the text out.
        ListViewNative.DRAWTEXTPARAMS margins = new()
        {
            Size = ListViewNative.DrawTextParamsSize,
            LeftMargin = _textLeftMargin,
            RightMargin = _textRightMargin
        };
        _ = ListViewNative.DrawTextEx(hdc, text, text.Length, ref bounds, ListViewNative.DtVCenter | ListViewNative.DtEndEllipsis | ListViewNative.DtNoPrefix, ref margins);
    }

    private Color HoverBackColor() => BackColor.GetBrightness() < 0.5f ? DarkHoverBackColor : LightHoverBackColor;

    protected override void OnColumnClick(ColumnClickEventArgs e)
    {
        if (_sortColumn == e.Column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = e.Column;
            _sortDescending = StartsDescending(_columns[e.Column].Title);
        }

        SetRows(_rows);
        UpdateColumnHeaders();
        SortChanged?.Invoke(this, EventArgs.Empty);
        base.OnColumnClick(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ListViewHitTestInfo hit = HitTest(e.Location);
            if (hit.Item is not null && !hit.Item.Selected)
            {
                SelectedIndices.Clear();
                hit.Item.Selected = true;
            }
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int next = HitTest(e.Location).Item?.Index ?? -1;
        if (next != _hotIndex)
        {
            InvalidateRow(_hotIndex);
            _hotIndex = next;
            InvalidateRow(_hotIndex);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hotIndex != -1)
        {
            InvalidateRow(_hotIndex);
            _hotIndex = -1;
        }

        base.OnMouseLeave(e);
    }

    private void InvalidateRow(int index)
    {
        if (index >= 0 && index < VirtualListSize)
        {
            Invalidate(GetItemRect(index));
        }
    }

    private void UpdateColumnHeaders()
    {
        for (int index = 0; index < _columns.Count; index++)
        {
            string arrow = index == _sortColumn ? (_sortDescending ? " ▼" : " ▲") : "";
            Columns[index].Text = _columns[index].Title + arrow;
        }
    }

    private int HorizontalScrollPosition() => IsHandleCreated ? ListViewNative.GetScrollPos(Handle, ListViewNative.SbHorz) : 0;

    private void RestoreHorizontalScroll(int position)
    {
        if (!IsHandleCreated || position <= 0)
        {
            return;
        }

        int current = ListViewNative.GetScrollPos(Handle, ListViewNative.SbHorz);
        int delta = position - current;
        if (delta != 0)
        {
            _ = ListViewNative.SendMessage(Handle, ListViewNative.LvmScroll, (IntPtr)delta, IntPtr.Zero);
        }
    }

    private static bool StartsDescending(string title) =>
        title.Equals("CPU", StringComparison.OrdinalIgnoreCase)
        || title.Equals("Memory", StringComparison.OrdinalIgnoreCase)
        || title.Equals("Disk", StringComparison.OrdinalIgnoreCase)
        || title.Equals("Threads", StringComparison.OrdinalIgnoreCase)
        || title.Equals("Handles", StringComparison.OrdinalIgnoreCase);

    private static int CompareNullable(IComparable? left, IComparable? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        if (left is string leftString && right is string rightString)
        {
            return string.Compare(leftString, rightString, StringComparison.OrdinalIgnoreCase);
        }

        return left.CompareTo(right);
    }
}

file static class ListViewNative
{
    public const int SbHorz = 0;
    public const int LvmGetItemRect = 0x100E;
    public const int LvmScroll = 0x1014;
    public const int LvmGetColumnWidth = 0x101D;
    public const int LvmGetTopIndex = 0x1027;
    public const int LvmGetCountPerPage = 0x1028;
    public const int LvmGetItemState = 0x102C;
    public const int LvirBounds = 0;
    public const int LvisSelected = 0x2;
    public const int WmGetFont = 0x0031;
    public const int CddsPrePaint = 0x1;
    public const int CddsItemPrePaint = 0x10001;
    public const nint CdrfDoDefault = 0x0;
    public const nint CdrfSkipDefault = 0x4;
    public const nint CdrfNotifyItemDraw = 0x20;
    public const int BkModeTransparent = 1;
    public const int DtVCenter = 0x4;
    public const int DtNoPrefix = 0x800;
    public const int DtEndEllipsis = 0x8000;
    private const int NmCustomDraw = -12;
    public static readonly IntPtr DcBrush = GetStockObject(18);
    public static readonly int DrawTextParamsSize = Marshal.SizeOf<DRAWTEXTPARAMS>();

    // NMHDR.code follows the sender's window handle and control id.
    public static bool IsCustomDraw(IntPtr notification) => Marshal.ReadInt32(notification, 2 * IntPtr.Size) == NmCustomDraw;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NMHDR
    {
        public IntPtr HwndFrom;
        public IntPtr IdFrom;
        public int Code;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NMCUSTOMDRAW
    {
        public NMHDR Header;
        public int DrawStage;
        public IntPtr Hdc;
        public RECT Bounds;
        public IntPtr ItemSpec;
        public int ItemState;
        public IntPtr ItemLParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRAWTEXTPARAMS
    {
        public int Size;
        public int TabLength;
        public int LeftMargin;
        public int RightMargin;
        public int LengthDrawn;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TEXTMETRIC
    {
        public int Height;
        public int Ascent;
        public int Descent;
        public int InternalLeading;
        public int ExternalLeading;
        public int AveCharWidth;
        public int MaxCharWidth;
        public int Weight;
        public int Overhang;
        public int DigitizedAspectX;
        public int DigitizedAspectY;
        public ushort FirstChar;
        public ushort LastChar;
        public ushort DefaultChar;
        public ushort BreakChar;
        public byte Italic;
        public byte Underlined;
        public byte StruckOut;
        public byte PitchAndFamily;
        public byte CharSet;
    }

    [DllImport("user32.dll")]
    public static extern int GetScrollPos(IntPtr hWnd, int nBar);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("user32.dll", EntryPoint = "DrawTextExW", CharSet = CharSet.Unicode)]
    public static extern int DrawTextEx(IntPtr hdc, string text, int length, ref RECT rect, int format, ref DRAWTEXTPARAMS parameters);

    [DllImport("gdi32.dll", EntryPoint = "ExtTextOutW", CharSet = CharSet.Unicode)]
    public static extern bool ExtTextOut(IntPtr hdc, int x, int y, int options, IntPtr rect, string text, int length, IntPtr dx);

    [DllImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", CharSet = CharSet.Unicode)]
    public static extern bool GetTextExtentPoint32(IntPtr hdc, string text, int length, out SIZE size);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    public static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    public static extern int SetDCBrushColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll", EntryPoint = "GetTextMetricsW")]
    public static extern bool GetTextMetrics(IntPtr hdc, out TEXTMETRIC metrics);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);
}
