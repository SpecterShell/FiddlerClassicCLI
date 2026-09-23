// Checks the standard GDI list renderer and inherited font metrics through bounded adapter refreshes.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void InterfaceListUsesNativeTextAndStableRowHeightAcrossFontChangesAndRefreshes(float scale) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus
        {
            BindMode = HttpBindModes.Selected, BindAddresses = new[] { "10.0.0.1" },
            AvailableInterfaces = new[]
            {
                new HttpInterfaceAddressDto { Address = "10.0.0.1", AdapterName = "Ethernet" },
                new HttpInterfaceAddressDto { Address = "10.0.0.2", AdapterName = "Wi-Fi" }
            }
        } };
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * scale);
        using var panel = new BridgeControlPanel(client) { Size = new Size(456, 1000) };
        CompleteRefresh(panel);
        var list = Named<InterfaceAddressList>(panel, "Selected IPv4 interfaces");
        _ = list.Handle;
        panel.Font = font;
        panel.PerformLayout();
        list.SetItemChecked(1, true);
        var rowHeight = list.ItemHeight;
        var textHeight = TextRenderer.MeasureText("Ag", font, Size.Empty, TextFormatFlags.SingleLine).Height;
        Assert.InRange(rowHeight, textHeight, textHeight + 8);
        for (var refresh = 0; refresh < 4; refresh++)
        {
            // Exercise both the unchanged snapshot and item-replacement refresh paths.
            if (refresh == 2) client.Service.AvailableInterfaces[0].AdapterName = "Ethernet adapter";
            CompleteRefresh(panel);
            Assert.False(list.UseCompatibleTextRendering);
            Assert.Equal(font, list.Font);
            Assert.Equal(Named<ComboBox>(panel, "Listener bind mode").Font, list.Font);
            Assert.Equal(rowHeight, list.ItemHeight);
            Assert.Equal(rowHeight, list.GetItemRectangle(0).Height);
            Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, list.SelectedAddresses);
            Assert.True(list.Parent!.ClientRectangle.Contains(list.Bounds));
        }
        Assert.Empty(client.MutationCalls);
        SaveServiceSnapshot(panel, $"interface-font-{scale}");
    });
}
