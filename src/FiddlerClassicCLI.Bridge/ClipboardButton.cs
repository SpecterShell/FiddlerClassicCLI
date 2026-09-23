// Draws a font-scaled clipboard glyph while retaining native button focus, text, and keyboard behavior.
using System.Drawing;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal sealed class ClipboardButton : Button
{
    private readonly ToolTip _hint = new ToolTip();
    internal string MnemonicText { get; set; } = string.Empty;
    private static readonly string[] Pixels =
    {
        "................", ".....######.....", ".....#....#.....", "...###....###...",
        "...#.######.#...", "...#........#...", "...#........#...", "...#..####..#...",
        "...#........#...", "...#..####..#...", "...#........#...", "...#..####..#...",
        "...#........#...", "...##########...", "................", "................"
    };

    internal ClipboardButton()
    {
        Text = "&Copy";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TextImageRelation = TextImageRelation.ImageBeforeText;
        ImageAlign = ContentAlignment.MiddleLeft;
        UseVisualStyleBackColor = true;
        _hint.SetToolTip(this, "Copy URL");
        UpdateImage();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateImage();
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        UpdateImage();
    }

    protected override bool ProcessMnemonic(char charCode)
    {
        if (Text.Length == 0 && CanSelect && IsMnemonic(charCode, MnemonicText))
        {
            PerformClick();
            return true;
        }
        return base.ProcessMnemonic(charCode);
    }

    public override Size GetPreferredSize(Size proposedSize)
        => GetPreferredSizeForText(Text);

    internal Size GetPreferredSizeForText(string text)
    {
        var preferred = base.GetPreferredSize(Size.Empty);
        // Native image buttons need room for a single text line, the image gap, and both borders.
        var textWidth = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.SingleLine).Width;
        preferred.Width = textWidth + (Image?.Width ?? 0) + Padding.Horizontal + 12;
        return preferred;
    }

    /// <summary>Owns a crisp pixel-grid image sized for the inherited font, including a high-DPI host.</summary>
    private void UpdateImage()
    {
        var size = 16 * Math.Max(1, (int)Math.Round(Font.Height / 16d));
        var bitmap = new Bitmap(size, size);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                bitmap.SetPixel(x, y, Pixels[y * 16 / size][x * 16 / size] == '#'
                    ? SystemColors.ControlText : Color.Transparent);
        var previous = Image;
        Image = bitmap;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hint.Dispose();
            var image = Image;
            Image = null;
            image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
