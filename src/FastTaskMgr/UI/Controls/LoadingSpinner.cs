using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace FastTaskMgr.UI.Controls;

internal sealed class LoadingSpinner : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 80 };
    private int _frame;
    private bool _active;

    public LoadingSpinner()
    {
        DoubleBuffered = true;
        Size = new Size(20, 20);
        _timer.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % 8;
            Invalidate();
        };
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
            {
                return;
            }

            _active = value;
            if (_active)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }

            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_active)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float centerX = ClientSize.Width / 2f;
        float centerY = ClientSize.Height / 2f;
        float radius = Math.Max(4, Math.Min(ClientSize.Width, ClientSize.Height) / 2f - 4);

        for (int index = 0; index < 8; index++)
        {
            int alpha = 50 + (((index + _frame) % 8) * 25);
            double angle = (Math.PI * 2 / 8) * index;
            float x = centerX + (float)Math.Cos(angle) * radius;
            float y = centerY + (float)Math.Sin(angle) * radius;
            using SolidBrush brush = new(Color.FromArgb(Math.Min(230, alpha), Color.FromArgb(0, 120, 215)));
            e.Graphics.FillEllipse(brush, x - 2, y - 2, 4, 4);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
