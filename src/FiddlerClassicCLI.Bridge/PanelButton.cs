// Keeps native action buttons the same height as clipboard buttons at the inherited font size.
using System.Drawing;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal class PanelButton : Button
{
    internal PanelButton()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        UseVisualStyleBackColor = true;
        Margin = PanelStyle.ActionMargin;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(text.Width + Padding.Horizontal + 12, PanelStyle.ButtonHeight(Font) + Padding.Vertical);
    }
}
