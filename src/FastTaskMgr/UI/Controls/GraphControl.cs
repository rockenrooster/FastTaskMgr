using System.ComponentModel;

namespace FastTaskMgr.UI.Controls;

internal sealed class GraphControl : Control
{
    private readonly List<double> _samples = new(GraphPainter.MaxSamples);
    private readonly GraphPainter _painter = new();

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
        GraphPainter.CopySamples(values, _samples);
        Invalidate();
    }

    public void AddSample(double value)
    {
        _samples.Add(Math.Clamp(value, 0, 100));
        if (_samples.Count > GraphPainter.MaxSamples)
        {
            _samples.RemoveAt(0);
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        _painter.Paint(e.Graphics, ClientRectangle, _samples, Compact, Caption, Font, ForeColor, LineColor, FillColor);
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
