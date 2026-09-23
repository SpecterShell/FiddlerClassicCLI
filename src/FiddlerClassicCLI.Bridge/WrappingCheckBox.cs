// Reserves enough native checkbox height for captions that wrap in a narrow, high-DPI pane.
using System.Drawing;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal sealed class WrappingCheckBox : CheckBox
{
    internal WrappingCheckBox()
    {
        AutoSize = true;
        CheckAlign = ContentAlignment.TopLeft;
        TextAlign = ContentAlignment.TopLeft;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var natural = base.GetPreferredSize(Size.Empty);
        if (proposedSize.Width <= 0 || proposedSize.Width >= natural.Width) return natural;
        var glyph = Math.Max(SystemInformation.MenuCheckSize.Width, Font.Height) + 6;
        var text = TextRenderer.MeasureText(Text, Font,
            new Size(Math.Max(1, proposedSize.Width - glyph - Padding.Horizontal), 0), TextFormatFlags.WordBreak);
        return new Size(proposedSize.Width, Math.Max(natural.Height, text.Height + Padding.Vertical + 6));
    }
}
