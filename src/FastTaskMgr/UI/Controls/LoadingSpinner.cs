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
        Size = UiScale.Px(20, 20);
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
        float dot = UiScale.Px(4);
        float radius = Math.Max(dot, Math.Min(ClientSize.Width, ClientSize.Height) / 2f - dot);

        for (int index = 0; index < 8; index++)
        {
            int alpha = 50 + (((index + _frame) % 8) * 25);
            double angle = (Math.PI * 2 / 8) * index;
            float x = centerX + (float)Math.Cos(angle) * radius;
            float y = centerY + (float)Math.Sin(angle) * radius;
            using SolidBrush brush = new(Color.FromArgb(Math.Min(230, alpha), Color.FromArgb(0, 120, 215)));
            e.Graphics.FillEllipse(brush, x - dot / 2, y - dot / 2, dot, dot);
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
