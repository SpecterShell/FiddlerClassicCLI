// Adds a non-focusable status light alongside the listener's textual status.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal sealed class ServiceStatusIndicator : Control
{
    private bool? _running;

    internal ServiceStatusIndicator()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        TabStop = false;
        AccessibleName = "MCP HTTP listener indicator";
        AccessibleRole = AccessibleRole.Graphic;
        Size = new Size(Font.Height, Font.Height);
        Running = null;
    }

    internal bool? Running
    {
        get => _running;
        set
        {
            _running = value;
            ForeColor = value.HasValue ? value.Value ? Color.FromArgb(35, 153, 111) : Color.FromArgb(222, 79, 99) : SystemColors.GrayText;
            AccessibleDescription = value.HasValue ? value.Value ? "Listening" : "Not listening" : "Status unknown";
            Invalidate();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Size = new Size(Font.Height, Font.Height);
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        Running = _running;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var inset = Math.Max(1, Height / 8);
        var bounds = new Rectangle(inset, inset, Math.Max(1, Width - 2 * inset - 1), Math.Max(1, Height - 2 * inset - 1));
        using var fill = new SolidBrush(ForeColor);
        using var border = new Pen(ControlPaint.Dark(ForeColor));
        var smoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillEllipse(fill, bounds);
        e.Graphics.DrawEllipse(border, bounds);
        e.Graphics.SmoothingMode = smoothing;
    }
}
