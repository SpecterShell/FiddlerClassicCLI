// Copies values with temporary feedback, content-sized width, and native keyboard and tooltip behavior.
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class ClipboardButton : PanelButton
{
    private const string CopiedCaption = "Copied!";
    private readonly ToolTip _hint = new ToolTip { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _feedbackTimer = new System.Windows.Forms.Timer { Interval = 1500 };
    private readonly Action<string> _writeClipboard;
    private string _caption = "&Copy";
    private bool _compact;
    private bool _copied;

    /// <summary>Reads the current value only when activated. Values never enter the tooltip.</summary>
    internal Func<string?>? GetCopyText { get; set; }
    internal string MnemonicText => _caption;

    internal string ToolTipText
    {
        get => _hint.GetToolTip(this);
        set => _hint.SetToolTip(this, value);
    }

    public override string Text
    {
        get => base.Text;
        set { _caption = value ?? string.Empty; UpdatePresentation(); }
    }

    internal bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            UpdatePresentation();
        }
    }

    internal ClipboardButton() : this(Clipboard.SetText) { }

    /// <summary>Creates a clipboard action with an injectable writer for isolated STA checks.</summary>
    /// <param name="writeClipboard">Writes the value or throws when the clipboard is unavailable.</param>
    internal ClipboardButton(Action<string> writeClipboard)
    {
        _writeClipboard = writeClipboard ?? throw new ArgumentNullException(nameof(writeClipboard));
        Text = "&Copy";
        TextImageRelation = TextImageRelation.ImageBeforeText;
        ToolTipText = "Copy to clipboard.";
        _feedbackTimer.Tick += (_, _) => RestoreCaption();
        UpdateImage();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (IsDisposed || Disposing) return;
        var text = GetCopyText?.Invoke();
        if (string.IsNullOrEmpty(text))
        {
            RestoreCaption();
            return;
        }
        try
        {
            _writeClipboard(text!);
        }
        catch (ExternalException)
        {
            RestoreCaption();
            // Keep potentially sensitive values out of error text and clipboard diagnostics.
            if (IsHandleCreated) _hint.Show("Clipboard is busy. Try again.", this, 2000);
            return;
        }
        _feedbackTimer.Stop();
        _copied = true;
        UpdatePresentation();
        _feedbackTimer.Start();
    }

    private void RestoreCaption()
    {
        _feedbackTimer.Stop();
        _copied = false;
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        base.Text = _copied ? CopiedCaption : _compact ? string.Empty : _caption;
        ImageAlign = _compact ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
        Image = _compact && _copied ? null : _clipboardImage;
        Parent?.PerformLayout(this, nameof(Text));
    }

    protected override bool ProcessMnemonic(char charCode)
    {
        if (CanSelect && IsMnemonic(charCode, MnemonicText))
        {
            PerformClick();
            return true;
        }
        return base.ProcessMnemonic(charCode);
    }

    public override Size GetPreferredSize(Size proposedSize)
        => GetPresentationSize(_compact);

    internal Size GetPresentationSize(bool compact) => MeasureContent(
        _copied ? CopiedCaption : compact ? string.Empty : _caption,
        compact && _copied ? 0 : _clipboardImage?.Width ?? 0);

    internal Size GetPreferredSizeForText(string text)
        => MeasureContent(text, _clipboardImage?.Width ?? 0);

    private Size MeasureContent(string text, int imageWidth)
    {
        var textWidth = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.SingleLine).Width;
        return new Size(textWidth + imageWidth + Padding.Horizontal + 12,
            PanelStyle.ButtonHeight(Font) + Padding.Vertical);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _feedbackTimer.Stop();
            _feedbackTimer.Dispose();
            _hint.Dispose();
            GetCopyText = null;
            Image = null;
            _clipboardImage?.Dispose();
            _clipboardImage = null;
        }
        base.Dispose(disposing);
    }
}
