// Owns the font-scaled pixel clipboard glyph independently of temporary confirmation text.
using System.Drawing;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class ClipboardButton
{
    private Bitmap? _clipboardImage;
    private static readonly string[] Pixels =
    {
        "................", ".....######.....", ".....#....#.....", "...###....###...",
        "...#.######.#...", "...#........#...", "...#........#...", "...#..####..#...",
        "...#........#...", "...#..####..#...", "...#........#...", "...#..####..#...",
        "...#........#...", "...##########...", "................", "................"
    };

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

    /// <summary>Replaces the owned image with a crisp pixel grid scaled to the inherited font.</summary>
    private void UpdateImage()
    {
        var size = PanelStyle.GlyphSize(Font);
        var bitmap = new Bitmap(size, size);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                bitmap.SetPixel(x, y, Pixels[y * 16 / size][x * 16 / size] == '#'
                    ? SystemColors.ControlText : Color.Transparent);
        var previous = _clipboardImage;
        _clipboardImage = bitmap;
        UpdatePresentation();
        previous?.Dispose();
    }
}
