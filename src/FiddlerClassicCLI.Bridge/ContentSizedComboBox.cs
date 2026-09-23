// Fits dropdown choices using the native text rectangle so DPI-scaled arrows cannot cover their text.
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class ContentSizedComboBox : ComboBox
{
    private bool _fitting;
    private int _choiceWidth;

    internal ContentSizedComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        AutoSize = true;
    }

    /// <summary>Measures all choices and reserves the native arrow, border, and text padding.</summary>
    internal void FitChoices()
    {
        if (_fitting || IsDisposed || Items.Count == 0) return;
        _fitting = true;
        try
        {
            var textWidth = Items.Cast<object>().Max(item => TextRenderer.MeasureText(
                item.ToString(), Font, Size.Empty, TextFormatFlags.SingleLine).Width);
            var chromeWidth = Math.Max(SystemInformation.VerticalScrollBarWidth, Font.Height)
                + 2 * SystemInformation.Border3DSize.Width;
            if (IsHandleCreated)
            {
                // Fiddler's native font and device context can differ from WinForms' managed measurement.
                textWidth = MeasureNativeChoices(textWidth);
                var info = new ComboBoxInfo { Size = Marshal.SizeOf(typeof(ComboBoxInfo)) };
                if (GetComboBoxInfo(Handle, ref info))
                    chromeWidth = Width - (info.Item.Right - info.Item.Left);
            }
            _choiceWidth = textWidth + chromeWidth;
            Width = _choiceWidth;
            DropDownWidth = Width;
        }
        finally { _fitting = false; }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var preferred = base.GetPreferredSize(proposedSize);
        preferred.Width = Math.Max(preferred.Width, _choiceWidth);
        return preferred;
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        base.SetBoundsCore(x, y, Math.Max(width, _choiceWidth), height, specified);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        FitChoices();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Refit after native font, theme, or per-monitor DPI changes, including host-assigned fonts.
        if (m.Msg == 0x0030 || m.Msg == 0x031A || m.Msg == 0x02E3) FitChoices();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitChoices();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        FitChoices();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public NativeRectangle Item, Button;
        public int ButtonState;
        public IntPtr ComboHandle, ItemHandle, ListHandle;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr combo, ref ComboBoxInfo info);
}
