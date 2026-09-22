// Draws and registers the CLI tab's terminal icon without external image assets.
using System.Drawing;
using System.Windows.Forms;

namespace FiddlerClassic.Bridge;

internal static class TerminalTabIcon
{
    private const string ImageKey = "FiddlerClassicCLI.Terminal";
    private static readonly Color Background = Color.FromArgb(31, 45, 61);
    private static readonly Color TitleBar = Color.FromArgb(0, 140, 149);
    private static readonly string[] Pixels =
    {
        "................",
        "................",
        ".TTTTTTTTTTTTTT.",
        ".TTTTTTTTTTTTTT.",
        ".NNNNNNNNNNNNNN.",
        ".NNNNNNNNNNNNNN.",
        ".NNWWNNNNNNNNNN.",
        ".NNNWWNNNNNNNNN.",
        ".NNNNWWNNNNNNNN.",
        ".NNNNWWNNNNNNNN.",
        ".NNNWWNNNWWWWNN.",
        ".NNWWNNNNWWWWNN.",
        ".NNNNNNNNNNNNNN.",
        ".NNNNNNNNNNNNNN.",
        "................",
        "................"
    };

    /// <summary>Assigns the terminal icon before or after a tab is added to its parent.</summary>
    /// <param name="page">The extension's tab, owned by the caller.</param>
    /// <param name="images">The host-owned image list; call on the Fiddler UI thread.</param>
    internal static void ApplyTo(TabPage page, ImageList images)
    {
        // .NET Framework cannot resolve ImageKey set before parenting; a numeric index works in either order.
        page.ImageIndex = images.Images.IndexOfKey(Register(images));
    }

    /// <summary>
    /// Registers one icon at the host image-list size, or reuses it after an extension reload.
    /// </summary>
    /// <param name="images">The host-owned image list; call on the Fiddler UI thread.</param>
    /// <returns>The tab's image key. The host owns the registered copy until its image list is disposed.</returns>
    internal static string Register(ImageList images)
    {
        if (images.Images.ContainsKey(ImageKey)) return ImageKey;

        // Materialize the native list so Add copies the bitmap before we dispose it.
        _ = images.Handle;
        using var bitmap = Draw(images.ImageSize);
        images.Images.Add(ImageKey, bitmap);
        // Do not remove this shared entry on unload: that would shift other extensions' image indexes.
        return ImageKey;
    }

    /// <summary>Draws the 16x16 pixel grid using nearest-neighbor sampling, without antialiasing.</summary>
    /// <param name="size">The host's image-list dimensions.</param>
    /// <returns>A bitmap owned by the caller.</returns>
    private static Bitmap Draw(Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height);
        for (var y = 0; y < size.Height; y++)
            for (var x = 0; x < size.Width; x++)
            {
                var color = Pixels[y * 16 / size.Height][x * 16 / size.Width] switch
                {
                    'N' => Background,
                    'T' => TitleBar,
                    'W' => Color.White,
                    _ => Color.Transparent
                };
                bitmap.SetPixel(x, y, color);
            }
        return bitmap;
    }
}
