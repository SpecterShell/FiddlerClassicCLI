// Checks icon scaling, native image ownership, and safe reuse of the shared image list.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassic.Bridge;

namespace FiddlerClassic.Bridge.Tests;

public sealed class TerminalTabIconTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IconAppearsWhenAssignedBeforeParenting(bool createHandleFirst)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit };
                using var tabs = new TabControl { ImageList = images, Size = new Size(250, 100) };
                if (createHandleFirst) tabs.CreateControl();
                using var page = new TabPage("Fiddler Classic CLI");
                TerminalTabIcon.ApplyTo(page, images);
                tabs.TabPages.Add(page);
                tabs.CreateControl();

                using var rendered = new Bitmap(tabs.Width, tabs.Height);
                tabs.DrawToBitmap(rendered, tabs.ClientRectangle);
                var header = tabs.GetTabRect(0);
                var titleBarPixels = 0;
                for (var y = header.Top; y < header.Bottom; y++)
                    for (var x = header.Left; x < header.Right; x++)
                        if (rendered.GetPixel(x, y).ToArgb() == Color.FromArgb(0, 140, 149).ToArgb())
                            titleBarPixels++;
                Assert.True(titleBarPixels >= 20, "The tab header must render the terminal icon, not just its title.");
                SaveSnapshot(rendered, $"terminal-tab-{createHandleFirst}.png");
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The tab rendering test did not finish.");
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(20, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    public void RegistersReadableIconOnceWithoutChangingExistingImages(int size, bool createHandleFirst)
    {
        using var images = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
        using var existing = new Bitmap(size, size);
        existing.SetPixel(0, 0, Color.Red);
        images.Images.Add("existing", existing);
        if (createHandleFirst) _ = images.Handle;

        var key = TerminalTabIcon.Register(images);
        Assert.Equal(key, TerminalTabIcon.Register(images));

        Assert.Equal(2, images.Images.Count);
        Assert.Equal(0, images.Images.IndexOfKey("existing"));
        Assert.Equal(1, images.Images.IndexOfKey(key));
        using var original = (Bitmap)images.Images[0];
        Assert.Equal(Color.Red.ToArgb(), original.GetPixel(0, 0).ToArgb());
        // Retrieving the native copy also catches premature disposal of a lazily added bitmap.
        using var icon = (Bitmap)images.Images[key];
        Assert.Equal(images.ImageSize, icon.Size);
        Assert.Equal(0, icon.GetPixel(0, 0).A);
        Assert.Equal(255, icon.GetPixel(size / 2, size / 2).A);
        Assert.Equal(Color.White.ToArgb(), icon.GetPixel(10 * size / 16, 11 * size / 16).ToArgb());
        var palette = new[] { Color.White.ToArgb(), Color.FromArgb(31, 45, 61).ToArgb(), Color.FromArgb(0, 140, 149).ToArgb() };
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var pixel = icon.GetPixel(x, y);
                Assert.True(pixel.A is 0 or 255, "Pixel edges must not have partial transparency.");
                if (pixel.A == 255) Assert.Contains(pixel.ToArgb(), palette);
            }
        SaveSnapshot(icon, $"terminal-icon-{size}.png");
    }

    private static void SaveSnapshot(Bitmap image, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_LAYOUT_SNAPSHOTS");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            image.Save(Path.Combine(directory, fileName));
        }
    }
}
