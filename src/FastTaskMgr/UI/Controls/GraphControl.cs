using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace FastTaskMgr.UI.Controls;

internal sealed class GraphControl : Control
{
    private const int MaxSamples = 120;
    private readonly List<double> _samples = new(MaxSamples);
    private PointF[] _points = [];
    private PointF[] _polygon = [];
    // GDI+ objects are cached and rebuilt only when their color changes; a Performance page
    // repaints dozens of graphs every tick.
    private Pen? _borderPen;
    private Pen? _gridPen;
    private Pen? _linePen;
    private SolidBrush? _fillBrush;

    public GraphControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color LineColor { get; set; } = Color.FromArgb(0, 120, 215);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.FromArgb(48, 0, 120, 215);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Compact { get; set; }

    public void SetSamples(IReadOnlyList<double> values)
    {
        _samples.Clear();
        for (int index = Math.Max(0, values.Count - MaxSamples); index < values.Count; index++)
        {
            _samples.Add(Math.Clamp(values[index], 0, 100));
        }

        Invalidate();
    }

    public void AddSample(double value)
    {
        _samples.Add(Math.Clamp(value, 0, 100));
        if (_samples.Count > MaxSamples)
        {
            _samples.RemoveAt(0);
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        Rectangle bounds = ClientRectangle;
        graphics.Clear(BackColor);

        // Axis-aligned 1px border and grid: crisper and cheaper without anti-aliasing.
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawRectangle(Cached(ref _borderPen, Color.FromArgb(130, ForeColor), 1f), bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);

        if (!Compact)
        {
            Pen gridPen = Cached(ref _gridPen, Color.FromArgb(35, ForeColor), 1f);
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

            TextRenderer.DrawText(graphics, Caption, Font, new Point(8, 8), ForeColor);
        }

        int count = _samples.Count;
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
            float y = bounds.Bottom - 1f - (float)(_samples[i] / 100d * (bounds.Height - 1));
            _points[i] = new PointF(x, y);
            _polygon[i] = _points[i];
        }

        _polygon[count] = new PointF(_points[^1].X, bounds.Bottom - 1);
        _polygon[count + 1] = new PointF(_points[0].X, bounds.Bottom - 1);

        // The fill's top edge sits under the anti-aliased line, so it can be filled without AA.
        if (_fillBrush is null || _fillBrush.Color != FillColor)
        {
            _fillBrush?.Dispose();
            _fillBrush = new SolidBrush(FillColor);
        }

        graphics.FillPolygon(_fillBrush, _polygon);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawLines(Cached(ref _linePen, LineColor, 2f), _points);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _borderPen?.Dispose();
            _gridPen?.Dispose();
            _linePen?.Dispose();
            _fillBrush?.Dispose();
        }

        base.Dispose(disposing);
    }
}
