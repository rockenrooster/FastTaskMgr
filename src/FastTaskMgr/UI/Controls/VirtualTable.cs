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
    // WinForms raises RetrieveVirtualItem for the row and again for every owner-drawn cell
    // (plus hit tests), so building the item on each request formatted every column ~40 times
    // per visible row per repaint. Items are cached until the next SetRows.
    private ListViewItem?[] _itemCache = [];
    private SolidBrush? _backBrush;
    private SolidBrush? _hoverBrush;
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
        OwnerDraw = true;
        UseCompatibleStateImageBehavior = false;
        View = View.Details;
        VirtualMode = true;
        DoubleBuffered = true;

        foreach (TableColumn<T> column in columns)
        {
            Columns.Add(column.Title, column.Width);
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

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawDefault = true;
        base.OnDrawColumnHeader(e);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        bool selected = e.Item?.Selected == true;
        Brush background = selected ? SystemBrushes.Highlight : e.ItemIndex == _hotIndex ? HoverBrush() : BackBrush();
        Color textColor = selected ? SystemColors.HighlightText : ForeColor;
        e.Graphics.FillRectangle(background, e.Bounds);
        Rectangle textBounds = new(e.Bounds.Left + 4, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 8), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem?.Text ?? "",
            Font,
            textBounds,
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        // No manual grid lines: the native GridLines pass draws over owner-drawn cells, and the
        // GDI+ DrawLine calls that duplicated it were the most expensive part of each cell.
        base.OnDrawSubItem(e);
    }

    // Brushes follow BackColor (theme switches); cached so cell painting allocates no GDI+ objects.
    private SolidBrush BackBrush()
    {
        if (_backBrush is null || _backBrush.Color != BackColor)
        {
            _backBrush?.Dispose();
            _backBrush = new SolidBrush(BackColor);
        }

        return _backBrush;
    }

    private SolidBrush HoverBrush()
    {
        Color hoverBackColor = BackColor.GetBrightness() < 0.5f ? DarkHoverBackColor : LightHoverBackColor;
        if (_hoverBrush is null || _hoverBrush.Color != hoverBackColor)
        {
            _hoverBrush?.Dispose();
            _hoverBrush = new SolidBrush(hoverBackColor);
        }

        return _hoverBrush;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _backBrush?.Dispose();
            _hoverBrush?.Dispose();
        }

        base.Dispose(disposing);
    }

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
    public const int LvmScroll = 0x1014;
    public const int LvmGetTopIndex = 0x1027;
    public const int LvmGetCountPerPage = 0x1028;

    [DllImport("user32.dll")]
    public static extern int GetScrollPos(IntPtr hWnd, int nBar);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
