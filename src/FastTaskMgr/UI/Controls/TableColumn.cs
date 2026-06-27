namespace FastTaskMgr.UI.Controls;

internal sealed record TableColumn<T>(
    string Title,
    int Width,
    Func<T, string> Text,
    Func<T, IComparable?>? SortKey = null);
