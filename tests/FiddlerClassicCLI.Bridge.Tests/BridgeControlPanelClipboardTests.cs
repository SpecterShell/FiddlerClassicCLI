// Checks copy feedback, tooltips, and disposal without reading or modifying the real clipboard.
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(false, 1f)]
    [InlineData(true, 1f)]
    [InlineData(false, 1.5f)]
    [InlineData(true, 1.5f)]
    [InlineData(false, 2f)]
    [InlineData(true, 2f)]
    public void CopyFeedbackFitsCurrentCaptionAndRestoresWidthAndShortcut(bool compact, float scale) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * scale);
        var copied = new List<string>();
        var value = "http://127.0.0.1:8877/mcp";
        var label = new SelectableAddress { Text = value };
        var copy = new ClipboardButton(copied.Add)
        {
            AccessibleName = "Copy test endpoint", GetCopyText = () => value
        };
        using var row = new EndpointRow(label, copy)
        {
            Font = font, Size = new Size(compact ? (int)(100 * scale) : 700, 300)
        };
        row.CreateControl();
        row.PerformLayout();
        Assert.Equal(compact, copy.Compact);
        var caption = copy.Text;
        var bounds = copy.Bounds;
        var preferred = copy.GetPreferredSize(Size.Empty);
        var tip = copy.ToolTipText;
        SaveLayoutSnapshot(row, $"copy-{compact}-{scale}-before");
        copy.PerformClick();
        Assert.Equal(new[] { value }, copied);
        Assert.Equal("Copied!", copy.Text);
        Assert.Equal("Copy test endpoint", copy.AccessibilityObject.Name);
        row.PerformLayout();
        Assert.True(copy.Width > bounds.Width);
        Assert.Equal(bounds.Height, copy.Height);
        Assert.True(row.ClientRectangle.Contains(copy.Bounds));
        Assert.NotEqual(preferred.Width, copy.GetPreferredSize(Size.Empty).Width);
        var textWidth = TextRenderer.MeasureText(copy.Text, copy.Font, Size.Empty, TextFormatFlags.SingleLine).Width;
        Assert.True(copy.Width >= textWidth + (copy.Image?.Width ?? 0) + copy.Padding.Horizontal + 8);
        SaveLayoutSnapshot(row, $"copy-{compact}-{scale}-after");

        value = "http://127.0.0.1:9001/mcp";
        var mnemonic = typeof(ClipboardButton).GetMethod("ProcessMnemonic",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.True((bool)mnemonic.Invoke(copy, new object[] { 'c' })!);
        Assert.Equal(value, copied.Last());
        Assert.Equal(2, copied.Count);
        Assert.Equal("Copied!", copy.Text);
        Assert.Equal(tip, copy.ToolTipText);
        AssertCopyCaptionRestored(copy, caption);
        row.PerformLayout();
        Assert.Equal(bounds, copy.Bounds);
        Assert.Equal(preferred, copy.GetPreferredSize(Size.Empty));
        Assert.Equal("Copy test endpoint", copy.AccessibilityObject.Name);
        Assert.NotNull(copy.Image);
    });

    [Fact]
    public void LongerCopyCaptionShrinksDuringFeedbackAndRestoresItsWidth() => RunOnSta(() =>
    {
        var address = new SelectableAddress { Text = @"\\.\pipe\test-bridge" };
        var copy = new ClipboardButton(_ => { }) { Text = "Copy bridge p&ipe", GetCopyText = () => address.Text };
        using var row = new EndpointRow(address, copy) { Size = new Size(800, 80) };
        row.CreateControl();
        row.PerformLayout();
        var originalWidth = copy.Width;
        copy.PerformClick();
        row.PerformLayout();
        Assert.True(copy.Width < originalWidth);
        Assert.Equal("Copied!", copy.Text);
        AssertCopyCaptionRestored(copy, "Copy bridge p&ipe");
        row.PerformLayout();
        Assert.Equal(originalWidth, copy.Width);
    });

    [Fact]
    public void CopyFailuresAndEmptyValuesNeverReportSuccess() => RunOnSta(() =>
    {
        var fail = false;
        using var copy = new ClipboardButton(_ =>
        {
            if (fail) throw new ExternalException("Synthetic clipboard contention");
        }) { GetCopyText = () => "synthetic-value" };
        copy.PerformClick();
        Assert.Equal("Copied!", copy.Text);
        fail = true;
        copy.PerformClick();
        Assert.Equal("&Copy", copy.Text);
        fail = false;
        copy.PerformClick();
        Assert.Equal("Copied!", copy.Text);
        copy.GetCopyText = () => string.Empty;
        copy.PerformClick();
        Assert.Equal("&Copy", copy.Text);
    });

    [Fact]
    public void DisposingACopyButtonStopsFeedbackAndReleasesTheValueProvider() => RunOnSta(() =>
    {
        var copy = new ClipboardButton(_ => { }) { GetCopyText = () => "synthetic-value" };
        copy.PerformClick();
        Assert.Equal("Copied!", copy.Text);
        copy.Dispose();
        Assert.Null(copy.GetCopyText);
        Assert.Null(copy.Image);
        var text = copy.Text;
        PumpUntilCompleted(Task.Delay(1700));
        Assert.Equal(text, copy.Text);
    });

    [Fact]
    public void PanelButtonsHaveActionTooltipsAndCopiesReadCurrentValues() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = AllInterfaceStatus() };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        Assert.All(Descendants(panel).OfType<Button>(), button =>
            Assert.False(string.IsNullOrWhiteSpace(panel.GetButtonToolTip(button)), button.AccessibleName));
        Assert.Contains("Enable or disable MCP HTTP", panel.GetButtonToolTip(Named<CheckBox>(panel, "Enable MCP HTTP service")));
        client.Service.Enabled = true;
        client.Service.Port = 9002;
        CompleteRefresh(panel);
        Assert.Contains("require confirmation", panel.GetButtonToolTip(Named<CheckBox>(panel, "Enable MCP HTTP service")));
        var endpoint = Named<ClipboardButton>(panel, "Copy loopback endpoint");
        Assert.Equal("http://127.0.0.1:9002/mcp", endpoint.GetCopyText!());
        Assert.All(Descendants(panel).OfType<ClipboardButton>().Where(copy => copy.Enabled), copy =>
            Assert.Equal(copy.AccessibleDescription, copy.GetCopyText!()));

        using var dialog = TextPromptDialog.CreateSecretForm("Test client", "synthetic-secret");
        var tokenCopy = Named<ClipboardButton>(dialog, "Copy bearer token");
        Assert.Equal("synthetic-secret", tokenCopy.GetCopyText!());
        Assert.DoesNotContain("synthetic-secret", tokenCopy.ToolTipText);
    });

    private static void AssertCopyCaptionRestored(ClipboardButton copy, string caption)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (copy.Text == "Copied!" && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Assert.Equal(caption, copy.Text);
    }
}
