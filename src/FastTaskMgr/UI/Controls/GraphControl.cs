using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace FastTaskMgr.UI.Controls;

internal sealed class GraphControl : Control
{
    private readonly List<double> _samples = [];

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

    public void SetSamples(IEnumerable<double> values)
    {
        _samples.Clear();
        _samples.AddRange(values.Select(value => Math.Clamp(value, 0, 100)).TakeLast(120));
        Invalidate();
    }

    public void AddSample(double value)
    {
        _samples.Add(Math.Clamp(value, 0, 100));
        if (_samples.Count > 120)
        {
            _samples.RemoveAt(0);
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle bounds = ClientRectangle;
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using Pen borderPen = new(Color.FromArgb(130, ForeColor));
        Rectangle border = new(bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);
        e.Graphics.DrawRectangle(borderPen, border);

        if (!Compact)
        {
            using Pen gridPen = new(Color.FromArgb(35, ForeColor));
            for (int i = 1; i < 4; i++)
            {
                int y = bounds.Top + bounds.Height * i / 4;
                e.Graphics.DrawLine(gridPen, bounds.Left, y, bounds.Right, y);
            }

            for (int i = 1; i < 8; i++)
            {
                int x = bounds.Left + bounds.Width * i / 8;
                e.Graphics.DrawLine(gridPen, x, bounds.Top, x, bounds.Bottom);
            }

            TextRenderer.DrawText(e.Graphics, Caption, Font, new Point(8, 8), ForeColor);
        }

        if (_samples.Count < 2)
        {
            return;
        }

        PointF[] points = new PointF[_samples.Count];
        for (int i = 0; i < _samples.Count; i++)
        {
            float x = bounds.Left + (bounds.Width - 1f) * i / Math.Max(1, _samples.Count - 1);
            float y = bounds.Bottom - 1f - (float)(_samples[i] / 100d * (bounds.Height - 1));
            points[i] = new PointF(x, y);
        }

        using GraphicsPath fillPath = new();
        fillPath.AddLines(points);
        fillPath.AddLine(points[^1].X, bounds.Bottom - 1, points[0].X, bounds.Bottom - 1);
        fillPath.CloseFigure();
        using SolidBrush fill = new(FillColor);
        e.Graphics.FillPath(fill, fillPath);

        using Pen linePen = new(LineColor, 2f);
        e.Graphics.DrawLines(linePen, points);
    }
}
