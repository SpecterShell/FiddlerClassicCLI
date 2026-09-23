// Displays a wrapping, read-only address with native mouse selection and keyboard copying.
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal sealed class SelectableAddress : TextBox
{
    private TextBox? _measure;
    private readonly Dictionary<int, Size> _sizes = new Dictionary<int, Size>();

    internal SelectableAddress()
    {
        ReadOnly = true;
        BorderStyle = BorderStyle.None;
        Multiline = true;
        WordWrap = true;
        ScrollBars = ScrollBars.None;
        AutoSize = false;
        TabStop = true;
        HideSelection = false;
        ShortcutsEnabled = true;
    }

    public override string Text
    {
        get => base.Text;
        set
        {
            // An unchanged poll must leave the user's selection and caret where they are.
            if (!string.Equals(base.Text, value ?? string.Empty, StringComparison.Ordinal))
                base.Text = value ?? string.Empty;
        }
    }

    /// <summary>Measures native word wrapping without moving the displayed caret or changing selection.</summary>
    /// <param name="proposedSize">The available width, or zero for a single line.</param>
    /// <returns>The text width and height needed to display every wrapped line.</returns>
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (_sizes.TryGetValue(proposedSize.Width, out var cached)) return cached;
        // Measure with an offscreen native edit control so long, unbroken pipe names wrap exactly.
        // Resizing the displayed control during measurement would disturb selection and re-enter layout.
        _measure ??= new TextBox { BorderStyle = BorderStyle.None, Multiline = true, WordWrap = true, ReadOnly = true };
        _measure.Font = Font;
        if (_measure.Text != Text) _measure.Text = Text;
        var nativeFont = IsHandleCreated ? SendMessage(Handle, 0x0031, IntPtr.Zero, IntPtr.Zero) : IntPtr.Zero; // WM_GETFONT
        using var measuredFont = nativeFont == IntPtr.Zero ? null : Font.FromHfont(nativeFont);
        var font = measuredFont ?? Font;
        var natural = TextRenderer.MeasureText(Text, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var width = Math.Max(1, proposedSize.Width > 0 ? Math.Min(proposedSize.Width, natural.Width) : natural.Width);
        // A native edit shorter than its font can retain an invalid wrapping rectangle.
        _measure.Size = new Size(width, Math.Max(Font.Height, font.Height) * 2);
        if (nativeFont != IntPtr.Zero) SendMessage(_measure.Handle, 0x0030, nativeFont, IntPtr.Zero); // WM_SETFONT
        var lines = _measure.GetLineFromCharIndex(_measure.TextLength) + 1;
        var size = new Size(width, lines * Math.Max(Font.Height, font.Height) + 2);
        if (_sizes.Count >= 32) _sizes.Clear();
        _sizes[proposedSize.Width] = size;
        return size;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        _sizes.Clear();
        base.OnTextChanged(e);
        Parent?.PerformLayout(this, nameof(Text));
    }

    protected override void OnFontChanged(EventArgs e)
    {
        _sizes.Clear();
        base.OnFontChanged(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        _sizes.Clear();
        base.OnHandleCreated(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0030) _sizes.Clear(); // WM_SETFONT
        base.WndProc(ref message);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.A))
        {
            SelectAll();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _measure?.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
