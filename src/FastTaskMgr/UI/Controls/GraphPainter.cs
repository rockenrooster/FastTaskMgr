using System.Drawing.Drawing2D;

namespace FastTaskMgr.UI.Controls;

/// <summary>Draws one utilization graph. Shared by <see cref="GraphControl"/> and <see cref="GraphGridControl"/>.</summary>
internal sealed class GraphPainter : IDisposable
{
    public const int MaxSamples = 120;
    private PointF[] _points = [];
    private PointF[] _polygon = [];
    // GDI+ objects are cached and rebuilt only when their color changes; a Performance page
    // repaints dozens of graphs every tick.
    private Pen? _borderPen;
    private Pen? _gridPen;
    private Pen? _linePen;
    private SolidBrush? _fillBrush;

    /// <summary>Keeps the newest <see cref="MaxSamples"/> values, clamped to 0-100.</summary>
    public static void CopySamples(IReadOnlyList<double> values, List<double> target)
    {
        target.Clear();
        for (int index = Math.Max(0, values.Count - MaxSamples); index < values.Count; index++)
        {
            target.Add(Math.Clamp(values[index], 0, 100));
        }
    }

    /// <summary>Paints over <paramref name="bounds"/>; the caller clears the background.</summary>
    public void Paint(Graphics graphics, Rectangle bounds, IReadOnlyList<double> samples, bool compact, string caption, Font font, Color foreColor, Color lineColor, Color fillColor)
    {
        // Axis-aligned 1px border and grid: crisper and cheaper without anti-aliasing.
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawRectangle(Cached(ref _borderPen, Color.FromArgb(130, foreColor), 1f), bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);

        if (!compact)
        {
            Pen gridPen = Cached(ref _gridPen, Color.FromArgb(35, foreColor), 1f);
            for (int i = 1; i < 4; i++)
            {
                int y = bounds.Top + bounds.Height * i / 4;
                graphics.DrawLine(gridPen, bounds.Left, y, bounds.Right, y);
            }

            for (int i = 1; i < 8; i++)
            {
                int x = bounds.Left + bounds.Width * i / 8;
                graphics.DrawLine(gridPen, x, bounds.Top, x, bounds.Bottom);
            }

            // Kept inside the graph so a caption never spills into a small neighbouring grid cell.
            Rectangle captionBounds = Rectangle.FromLTRB(bounds.Left + UiScale.Px(8), bounds.Top + UiScale.Px(8), bounds.Right - 1, bounds.Bottom - 1);
            TextRenderer.DrawText(graphics, caption, font, captionBounds, foreColor, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        int count = samples.Count;
        if (count < 2)
        {
            return;
        }

        if (_points.Length != count)
        {
            _points = new PointF[count];
            _polygon = new PointF[count + 2];
        }

        for (int i = 0; i < count; i++)
        {
            float x = bounds.Left + (bounds.Width - 1f) * i / (count - 1);
            float y = bounds.Bottom - 1f - (float)(samples[i] / 100d * (bounds.Height - 1));
            _points[i] = new PointF(x, y);
            _polygon[i] = _points[i];
        }

        _polygon[count] = new PointF(_points[^1].X, bounds.Bottom - 1);
        _polygon[count + 1] = new PointF(_points[0].X, bounds.Bottom - 1);

        // The fill's top edge sits under the anti-aliased line, so it can be filled without AA.
        if (_fillBrush is null || _fillBrush.Color != fillColor)
        {
            _fillBrush?.Dispose();
            _fillBrush = new SolidBrush(fillColor);
        }

        graphics.FillPolygon(_fillBrush, _polygon);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawLines(Cached(ref _linePen, lineColor, 2f), _points);
    }

    private static Pen Cached(ref Pen? pen, Color color, float width)
    {
        if (pen is null || pen.Color != color)
        {
            pen?.Dispose();
            pen = new Pen(color, width);
        }

        return pen;
    }

    public void Dispose()
    {
        _borderPen?.Dispose();
        _gridPen?.Dispose();
        _linePen?.Dispose();
        _fillBrush?.Dispose();
    }
}
