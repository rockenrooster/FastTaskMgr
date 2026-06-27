namespace FastTaskMgr.UI.Controls;

internal sealed class VirtualTable<T> : ListView
    where T : class
{
    private readonly IReadOnlyList<TableColumn<T>> _columns;
    private readonly Func<T, object?> _keySelector;
    private IReadOnlyList<T> _rows = [];
    private int _sortColumn;
    private bool _sortDescending;

    public VirtualTable(IReadOnlyList<TableColumn<T>> columns, Func<T, object?>? keySelector = null)
    {
        _columns = columns;
        _keySelector = keySelector ?? (row => row);
        Dock = DockStyle.Fill;
        FullRowSelect = true;
        GridLines = true;
        HideSelection = false;
        MultiSelect = true;
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

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        T row = _rows[e.ItemIndex];
        string[] subItems = _columns.Select(column => column.Text(row)).ToArray();
        e.Item = new ListViewItem(subItems);
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
            _sortDescending = false;
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

    private void UpdateColumnHeaders()
    {
        for (int index = 0; index < _columns.Count; index++)
        {
            string arrow = index == _sortColumn ? (_sortDescending ? " ▼" : " ▲") : "";
            Columns[index].Text = _columns[index].Title + arrow;
        }
    }

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
