using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace FastTaskMgr.UI.Controls;

/// <summary>
/// A grid of graphs (one per CPU core) drawn by a single control. One window, paint and back
/// buffer per tick, instead of a layout panel of graph controls that each repainted on their own.
/// </summary>
internal sealed class GraphGridControl : Control
{
    private static readonly int CellMargin = UiScale.Px(2);
    private readonly GraphPainter _painter = new();
    private readonly List<List<double>> _samples = [];
    private string[] _captions = [];
    private int _columns = 1;

    public GraphGridControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color LineColor { get; set; } = Color.FromArgb(0, 120, 215);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.FromArgb(48, 0, 120, 215);

    public void SetGraphs(int count, int columns, Func<int, string> caption, Func<int, IReadOnlyList<double>> samples)
    {
        if (_captions.Length != count)
        {
            _captions = new string[count];
            for (int index = 0; index < count; index++)
            {
                _captions[index] = caption(index);
            }
        }

        while (_samples.Count < count)
        {
            _samples.Add(new List<double>(GraphPainter.MaxSamples));
        }

        _samples.RemoveRange(count, _samples.Count - count);
        _columns = Math.Max(1, columns);
        for (int index = 0; index < count; index++)
        {
            GraphPainter.CopySamples(samples(index), _samples[index]);
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        int rows = (_captions.Length + _columns - 1) / _columns;
        for (int index = 0; index < _captions.Length; index++)
        {
            Rectangle cell = CellBounds(index % _columns, index / _columns, rows);
            if (cell.Width > 0 && cell.Height > 0 && e.ClipRectangle.IntersectsWith(cell))
            {
                // The anti-aliased line spills a pixel past the graph's edges; clip it to the
                // cell the way a separate control's bounds did.
                GraphicsState state = e.Graphics.Save();
                e.Graphics.SetClip(cell, CombineMode.Intersect);
                _painter.Paint(e.Graphics, cell, _samples[index], compact: false, _captions[index], Font, ForeColor, LineColor, FillColor);
                e.Graphics.Restore(state);
            }
        }
    }

    // Same layout as the TableLayoutPanel of docked graphs this replaces: equal cells, the last
    // column and row take the leftover pixels, and each graph keeps a margin.
    private Rectangle CellBounds(int column, int row, int rows)
    {
        Size size = ClientSize;
        int width = size.Width / _columns;
        int height = size.Height / rows;
        int left = column * width;
        int top = row * height;
        int right = column == _columns - 1 ? size.Width : left + width;
        int bottom = row == rows - 1 ? size.Height : top + height;
        return Rectangle.FromLTRB(left + CellMargin, top + CellMargin, right - CellMargin, bottom - CellMargin);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _painter.Dispose();
        }

        base.Dispose(disposing);
    }
}
