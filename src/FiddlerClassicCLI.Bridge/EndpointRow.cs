// Keeps a wrapping URL or named-pipe endpoint and its clipboard button on the same row.
using System.Drawing;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal sealed class EndpointRow : Panel
{
    private readonly SelectableAddress _endpoint;
    private readonly ClipboardButton _copy;
    private bool _layingOut;
    /// <summary>Transfers ownership of a selectable address and its adjacent copy button to this row.</summary>
    /// <param name="endpoint">The full read-only address. Its text can wrap as the pane narrows.</param>
    /// <param name="copy">A copy action with an explicit accessible name for this address.</param>
    internal EndpointRow(SelectableAddress endpoint, ClipboardButton copy)
    {
        _endpoint = endpoint;
        _copy = copy;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        TabStop = false;
        endpoint.TabIndex = 0;
        copy.TabIndex = 1;
        Controls.Add(endpoint);
        Controls.Add(copy);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var copySize = MeasureCopy(proposedSize.Width, out _);
        var textSize = MeasureEndpoint(proposedSize.Width, copySize.Width);
        return new Size(textSize.Width + _endpoint.Margin.Horizontal + copySize.Width + _copy.Margin.Horizontal,
            Math.Max(textSize.Height + _endpoint.Margin.Vertical, copySize.Height + _copy.Margin.Vertical));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_layingOut || _endpoint is null || _copy is null) return;
        _layingOut = true;
        try
        {
            var copySize = MeasureCopy(ClientSize.Width, out var compact);
            _copy.Compact = compact;
            var textSize = MeasureEndpoint(ClientSize.Width, copySize.Width);
            _endpoint.Bounds = new Rectangle(_endpoint.Margin.Left,
                Math.Max(_endpoint.Margin.Top, (ClientSize.Height - textSize.Height) / 2), textSize.Width, textSize.Height);
            _copy.Bounds = new Rectangle(_endpoint.Right + _endpoint.Margin.Right + _copy.Margin.Left,
                Math.Max(_copy.Margin.Top, (ClientSize.Height - copySize.Height) / 2), copySize.Width, copySize.Height);
        }
        finally { _layingOut = false; }
    }

    private Size MeasureCopy(int availableWidth, out bool compact)
    {
        var fullSize = _copy.GetPreferredSizeForText(_copy.MnemonicText);
        var minimumText = TextRenderer.MeasureText("MMMMMM", _endpoint.Font).Width;
        // Keep a readable address beside the action in a narrow, high-DPI pane.
        compact = availableWidth > 0 && availableWidth < fullSize.Width + minimumText
            + _copy.Margin.Horizontal + _endpoint.Margin.Horizontal;
        return _copy.GetPresentationSize(compact);
    }

    private Size MeasureEndpoint(int availableWidth, int copyWidth)
    {
        var textWidth = availableWidth > 0
            ? Math.Max(1, availableWidth - copyWidth - _copy.Margin.Horizontal - _endpoint.Margin.Horizontal) : 0;
        return _endpoint.GetPreferredSize(new Size(textWidth, 0));
    }
}
