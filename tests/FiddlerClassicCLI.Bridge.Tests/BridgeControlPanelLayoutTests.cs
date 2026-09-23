// Checks wrapping actions, aligned service rows, and readable versions across pane and font sizes.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(8.25f)]
    [InlineData(12f)]
    [InlineData(16f)]
    public void ResizingKeepsActionsVisibleAndServiceRowsAligned(float fontSize) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, fontSize);
        var client = new FakeHostControlClient
        {
            PipeName = "fiddler-classic-cli.v3.0123456789abcdef.daemon.v1",
            Service = new HttpServiceStatus { Endpoint = "http://127.0.0.1:8877/mcp" },
            Daemon = RunningDaemon(),
            LaunchRecord = new HostLaunchRecord { HostVersion = "0.3.0-preview.1+" + new string('a', 40) }
        };
        client.Daemon.HostVersion = "0.3.0-preview.1+" + new string('a', 40);
        using var panel = new BridgeControlPanel(client, bridgeStatus: () =>
            new BridgePipeStatus("fiddler-classic-cli.v3.0123456789abcdef", BridgeListenerState.Listening)) { Font = font };
        panel.CreateControl();
        PumpUntilIdle(panel);
        CompleteRefresh(panel);
        foreach (var width in new[] { 1200, 456, 320, 900 })
        {
            panel.Size = new Size(width, 1000);
            Application.DoEvents();
            panel.PerformLayout();
            Assert.True(panel.Controls[0].Right <= panel.ClientSize.Width, "Content must stay within the pane width.");
            if (panel.Controls[0].Height > panel.ClientSize.Height)
                Assert.True(panel.VerticalScroll.Visible, "Sections below the viewport must remain scrollable.");
            foreach (var button in Descendants(panel).OfType<Button>())
            {
                for (Control child = button; child.Parent is not null && child.Parent != panel; child = child.Parent)
                {
                    Assert.True(child.Parent.ClientRectangle.Contains(child.Bounds),
                        $"'{button.Text}' is clipped by {child.Parent.GetType().Name} at width {width}, font {fontSize}: {child.Bounds} / {child.Parent.ClientRectangle}, preferred {child.Parent.GetPreferredSize(new Size(child.Parent.Width, 0))}.");
                }
            }

            var service = Descendants(panel).OfType<GroupBox>().Single(group => group.Text == "MCP HTTP service");
            var rows = Assert.Single(service.Controls.OfType<TableLayoutPanel>());
            for (var row = 0; row < 4; row++)
            {
                var caption = rows.GetControlFromPosition(0, row)!;
                var value = rows.GetControlFromPosition(1, row)!;
                Assert.True(Math.Abs((caption.Top + caption.Height / 2) - (value.Top + value.Height / 2)) <= 1,
                    $"Row {row} at width {width}, font {fontSize}: caption {caption.Bounds}, value {value.Bounds}.");
                Assert.True(value.Right <= rows.ClientSize.Width, $"Service value in row {row} extends outside the pane.");
            }

            foreach (var label in Descendants(panel).OfType<Label>())
            {
                Assert.True(label.Right <= label.Parent!.ClientSize.Width,
                    $"Text '{label.Text}' at {width}/{fontSize} extends outside {label.Parent.GetType().Name}: {label.Bounds} / {label.Parent.ClientSize}.");
                Assert.True(label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height,
                    "Wrapped text needs enough height for every line.");
            }

            SaveLayoutSnapshot(panel, width, fontSize);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongServiceErrorGrowsItsSectionWithoutCoveringTheClients(bool failedRequest) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var panel = new BridgeControlPanel(client) { Size = new Size(320, 700) };
        CompleteRefresh(panel);
        var service = Descendants(panel).OfType<GroupBox>().Single(group => group.Text == "MCP HTTP service");
        var initialHeight = service.Height;
        client.Service.LastError = string.Join(" ", Enumerable.Repeat("The host is unavailable. Check its configuration.", 8));
        if (failedRequest) client.Failure = new InvalidOperationException(client.Service.LastError);
        CompleteRefresh(panel);
        panel.PerformLayout();
        var error = Descendants(service).OfType<Label>().Single(label => label.Text == panel.ServiceErrorText);
        var clients = Descendants(panel).OfType<GroupBox>().Single(group => group.Text == "Authorized clients");

        Assert.True(service.Height > initialHeight);
        Assert.True(error.Height >= error.GetPreferredSize(new Size(error.Width, 0)).Height);
        Assert.True(clients.Top >= service.Bottom);
        Assert.True(panel.VerticalScroll.Visible);
    });

    private static void SaveLayoutSnapshot(Control panel, int width, float fontSize)
    {
        var directory = Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_LAYOUT_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(bitmap, panel.ClientRectangle);
        bitmap.Save(Path.Combine(directory, $"panel-{width}-{fontSize:0.##}.png"));
        var pipes = Named<GroupBox>(panel, "Named pipes");
        using var pipesBitmap = new Bitmap(pipes.Width, pipes.Height);
        pipes.DrawToBitmap(pipesBitmap, pipes.ClientRectangle);
        pipesBitmap.Save(Path.Combine(directory, $"pipes-{width}-{fontSize:0.##}.png"));
    }

    [Theory]
    [InlineData(8.25f)]
    [InlineData(12f)]
    [InlineData(16f)]
    public void VersionsRespectGroupContentBoundsAtDifferentFontSizes(float fontSize) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, fontSize);
        using var panel = new BridgeControlPanel(new FakeHostControlClient())
        {
            Font = font,
            Size = new Size(1200, 1000)
        };
        CompleteRefresh(panel);
        panel.PerformLayout();
        var group = Descendants(panel).OfType<GroupBox>().Single(control => control.Text == "Versions");
        group.PerformLayout();
        var label = Assert.Single(Descendants(group).OfType<Label>());
        var labelOrigin = label.Location;
        for (var parent = label.Parent; parent != group; parent = parent!.Parent)
            labelOrigin.Offset(parent!.Location);
        var groupBounds = new Rectangle(labelOrigin, label.Size);
        var contentBounds = group.DisplayRectangle;

        Assert.True(groupBounds.Top >= contentBounds.Top, "Version text must not cover the group heading.");
        Assert.True(groupBounds.Left >= contentBounds.Left, "Version text must respect the group padding.");
        Assert.True(groupBounds.Bottom <= contentBounds.Bottom, "The group must fit every version line.");
        Assert.Contains("Running daemon:", label.Text);
    });
}
