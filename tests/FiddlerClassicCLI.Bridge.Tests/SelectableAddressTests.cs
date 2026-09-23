// Checks native address selection, wrapping, and refresh preservation without changing the clipboard.
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Fact]
    public void AddressMeasurementFollowsFontAndTextChangesAfterHandleCreation() => RunOnSta(() =>
    {
        using var address = new SelectableAddress();
        address.CreateControl();
        foreach (var scale in new[] { 1f, 2f, 1.5f, 1f })
        {
            using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * scale);
            address.Font = font;
            foreach (var text in new[] { @"\\.\pipe\" + new string('p', 200), "http://127.0.0.1:8877/mcp" })
            {
                address.Text = text;
                foreach (var width in new[] { 900, 120, 450, 0 })
                {
                    address.Size = address.GetPreferredSize(new Size(width, 0));
                    var lines = address.GetLineFromCharIndex(address.TextLength) + 1;
                    Assert.InRange(address.Height, lines * font.Height, (lines + 1) * font.Height);
                    if (width == 0) Assert.Equal(1, lines);
                }
            }
        }
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void AddressesStaySelectableAndFullyVisibleAcrossNarrowLayouts(float scale) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * scale);
        var client = new FakeHostControlClient { Service = AllInterfaceStatus(), PipeName = new string('d', 200) };
        using var panel = new BridgeControlPanel(client, bridgeStatus: () =>
            new BridgePipeStatus(new string('b', 200), BridgeListenerState.Listening)) { Font = font };
        CompleteRefresh(panel);
        var addresses = Descendants(panel).OfType<SelectableAddress>().ToArray();
        Assert.Equal(5, addresses.Length);
        foreach (var address in addresses)
        {
            address.CreateControl();
            Assert.True(address.ReadOnly);
            Assert.True(address.ShortcutsEnabled);
            Assert.True(address.TabStop);
            Assert.True(address.WordWrap);
            Assert.False(address.AcceptsTab);
            Assert.False(address.HideSelection);
            Assert.Equal(BorderStyle.None, address.BorderStyle);
            address.Select(3, 7);
        }
        foreach (var width in new[] { 900, 456, 320, 1200 })
        {
            panel.Size = new Size(width, 1200);
            panel.PerformLayout();
            LayoutAllTabs(panel);
            CompleteRefresh(panel);
            AssertContentFits(panel);
            var endpointRow = Named<SelectableAddress>(panel, "Loopback endpoint").Parent!;
            var serviceLayout = (TableLayoutPanel)endpointRow.Parent!;
            if (width >= 900) Assert.Equal(1, serviceLayout.GetColumnSpan(endpointRow));
            if (width == 320 && scale >= 1.5f) Assert.Equal(2, serviceLayout.GetColumnSpan(endpointRow));
            foreach (var address in addresses)
            {
                Assert.Contains(address, Descendants(panel));
                Assert.Equal(3, address.SelectionStart);
                Assert.Equal(7, address.SelectionLength);
                Assert.Equal(address.Text.Substring(3, 7), address.SelectedText);
                var lines = address.GetLineFromCharIndex(address.TextLength) + 1;
                var natural = address.GetPreferredSize(Size.Empty);
                Assert.InRange(natural.Height, address.Font.Height, address.Font.Height * 2);
                Assert.True(address.ClientSize.Height >= lines * address.Font.Height,
                    $"{address.AccessibleName} at {width}/{scale}: {lines} native lines need more height than {address.Height}.");
                var copy = Assert.Single(address.Parent!.Controls.OfType<ClipboardButton>());
                Assert.Equal(address.Text, copy.GetCopyText!());
            }
            SaveLayoutSnapshot(Named<TableLayoutPanel>(panel, "Named pipes"), $"selectable-pipes-{scale}-{width}");
        }
        var loopback = Named<SelectableAddress>(panel, "Loopback endpoint");
        var keyHandler = typeof(SelectableAddress).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)keyHandler.Invoke(loopback, new object[] { new Message(), Keys.Control | Keys.A })!);
        Assert.Equal(loopback.Text, loopback.SelectedText);
        client.Service.Port = 9999;
        CompleteRefresh(panel);
        Assert.Equal("http://127.0.0.1:9999/mcp", loopback.Text);
    });
}
