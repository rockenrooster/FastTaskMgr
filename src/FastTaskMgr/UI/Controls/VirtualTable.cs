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
    private int _sortColumn;
    private bool _sortDescending;
    private int _hotIndex = -1;

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

    public void SetRows(IEnumerable<T> rows)
    {
        int horizontalScroll = HorizontalScrollPosition();
        BeginUpdate();
        try
        {
            HashSet<object> selectedKeys = SelectedIndices
                .Cast<int>()
                .Where(index => index >= 0 && index < _rows.Count)
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
            VirtualListSize = _rows.Count;
            SelectedIndices.Clear();

            if (selectedKeys.Count > 0)
            {
                for (int index = 0; index < _rows.Count; index++)
                {
                    object? rowKey = _keySelector(_rows[index]);
                    if (rowKey is not null && selectedKeys.Contains(rowKey))
                    {
                        SelectedIndices.Add(index);
                    }
                }
            }

            Invalidate();
        }
        finally
        {
            EndUpdate();
            RestoreHorizontalScroll(horizontalScroll);
        }
    }

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        T row = _rows[e.ItemIndex];
        string[] subItems = new string[_columns.Count];
        for (int index = 0; index < _columns.Count; index++)
        {
            subItems[index] = _columns[index].Text(row);
        }

        e.Item = new ListViewItem(subItems);
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawDefault = true;
        base.OnDrawColumnHeader(e);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        base.OnDrawItem(e);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        bool selected = e.Item?.Selected == true;
        Color hoverBackColor = BackColor.GetBrightness() < 0.5f ? DarkHoverBackColor : LightHoverBackColor;
        Color backColor = selected ? SystemColors.Highlight : e.ItemIndex == _hotIndex ? hoverBackColor : BackColor;
        Color textColor = selected ? SystemColors.HighlightText : ForeColor;
        using SolidBrush background = new(backColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        Rectangle textBounds = new(e.Bounds.Left + 4, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 8), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem?.Text ?? "",
            Font,
            textBounds,
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using Pen grid = new(SystemColors.ControlLight);
        e.Graphics.DrawLine(grid, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        e.Graphics.DrawLine(grid, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
        base.OnDrawSubItem(e);
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

    [DllImport("user32.dll")]
    public static extern int GetScrollPos(IntPtr hWnd, int nBar);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
