// Checks authentication-mode mapping, access notices, and credential actions without a live listener.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(0, HttpAuthenticationModes.Required)]
    [InlineData(1, HttpAuthenticationModes.NonLoopback)]
    [InlineData(2, HttpAuthenticationModes.None)]
    public void AuthenticationChoicesSaveExactModesWithoutSavingStagedBindings(int index, string mode) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus
        {
            AuthenticationMode = mode == HttpAuthenticationModes.NonLoopback ? HttpAuthenticationModes.Required : HttpAuthenticationModes.NonLoopback
        } };
        var warnings = new List<string>();
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { warnings.Add(message); return true; });
        CompleteRefresh(panel);
        Named<NumericUpDown>(panel, "Listener port").Value = 9500;
        Named<ComboBox>(panel, "Listener bind mode").SelectedItem = HttpBindModes.All;
        SelectTab(panel, "Settings");
        var authentication = Named<ComboBox>(panel, "MCP authentication mode");
        authentication.SelectedIndex = index;
        CompleteRefresh(panel);
        Assert.Equal(index, authentication.SelectedIndex);
        Button(panel, "Save settings").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal(mode, client.LastConfiguration!.AuthenticationMode);
        Assert.Equal(mode, client.Service.AuthenticationMode);
        Assert.Null(client.LastConfiguration.StartupMode);
        Assert.Null(client.LastConfiguration.BindMode);
        Assert.Null(client.LastConfiguration.BindAddresses);
        Assert.Null(client.LastConfiguration.Port);
        Assert.Equal(mode == HttpAuthenticationModes.None, client.LastConfiguration.Confirm);
        Assert.Equal(mode == HttpAuthenticationModes.None ? 1 : 0, warnings.Count);
        Assert.False(Button(panel, "Save settings").Enabled);
        Assert.Equal(HttpBindModes.All, Named<ComboBox>(panel, "Listener bind mode").SelectedItem);
        Assert.Equal(9500, Named<NumericUpDown>(panel, "Listener port").Value);
        Assert.False(Named<CheckBox>(panel, "Enable MCP HTTP service").Enabled);
    });

    [Theory]
    [InlineData(HttpBindModes.Loopback)]
    [InlineData(HttpBindModes.Selected)]
    public void NonLoopbackModeCanSaveAutomaticStartupAndEnableOnLoopbackWithoutWarnings(string bindMode) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus
        {
            BindMode = bindMode, BindAddresses = new[] { "127.0.0.1", "127.0.0.2" },
            AuthenticationMode = HttpAuthenticationModes.Required
        } };
        var warnings = new List<string>();
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { warnings.Add(message); return false; });
        CompleteRefresh(panel);
        SelectTab(panel, "Settings");
        Named<ComboBox>(panel, "MCP authentication mode").SelectedIndex = 1;
        Named<ComboBox>(panel, "MCP startup mode").SelectedItem = HttpStartupModes.Enabled;
        Button(panel, "Save settings").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal(HttpAuthenticationModes.NonLoopback, client.Service.AuthenticationMode);
        Assert.Equal(HttpStartupModes.Enabled, client.Service.StartupMode);
        Assert.False(client.LastConfiguration!.Confirm);
        SelectTab(panel, "MCP");
        Named<CheckBox>(panel, "Enable MCP HTTP service").Checked = true;
        PumpUntilIdle(panel);
        Assert.True(client.Service.Enabled);
        Assert.False(client.LastConfirmation);
        Assert.Empty(warnings);
    });

    [Fact]
    public void NoticesAndCredentialActionsFollowSavedModeAcrossRefreshes() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient
        {
            Clients = new ListHttpClientsResponse { Clients = new List<AuthorizedHttpClientDto>
            {
                new AuthorizedHttpClientDto { ClientId = "default", Name = "Default CLI" }
            } }
        };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var grid = Named<DataGridView>(panel, "Authorized clients");
        grid.CurrentCell = grid.Rows[0].Cells[0];
        grid.Rows[0].Selected = true;
        var notice = Named<Label>(panel, "Authentication warning");
        var authentication = Named<ComboBox>(panel, "MCP authentication mode");
        // Include the return from red no-authentication mode to the neutral default notice.
        foreach (var mode in new[] { HttpAuthenticationModes.NonLoopback, HttpAuthenticationModes.None,
                     HttpAuthenticationModes.NonLoopback, HttpAuthenticationModes.Required })
        {
            client.Service.AuthenticationMode = mode;
            CompleteRefresh(panel);
            Assert.Equal(mode == HttpAuthenticationModes.Required ? 0 : mode == HttpAuthenticationModes.NonLoopback ? 1 : 2,
                authentication.SelectedIndex);
            Assert.Equal(mode != HttpAuthenticationModes.None, Button(panel, "Authorize client").Enabled);
            Assert.Equal(mode != HttpAuthenticationModes.None, Button(panel, "Deauthorize").Enabled);
            Assert.Equal(mode != HttpAuthenticationModes.None, Button(panel, "Rotate default token").Enabled);
            Assert.Equal(mode != HttpAuthenticationModes.Required, notice.Visible);
            if (mode == HttpAuthenticationModes.None)
            {
                Assert.Equal(Color.DarkRed, notice.ForeColor);
                Assert.Contains("Anyone who can reach the listener has full MCP access", notice.Text);
            }
            else
            {
                Assert.Equal(SystemColors.ControlText, notice.ForeColor);
                if (mode == HttpAuthenticationModes.NonLoopback)
                {
                    Assert.Contains("loopback addresses at both ends have full MCP access without authentication", notice.Text);
                    Assert.Contains("Other connections require a bearer token", notice.Text);
                }
                else Assert.Empty(notice.Text);
            }
        }
        authentication.SelectedIndex = 2;
        CompleteRefresh(panel);
        Assert.Equal(2, authentication.SelectedIndex);
        Assert.Equal(HttpAuthenticationModes.Required, client.Service.AuthenticationMode);
        Assert.Empty(notice.Text);
        Assert.True(Button(panel, "Authorize client").Enabled);
        Assert.Equal(0, client.CredentialActionCount);
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void AuthenticationChoicesFitNativeTextAfterFontChangesAndRefreshes(float scale) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * scale);
        using var panel = new BridgeControlPanel(new FakeHostControlClient()) { Size = new Size(320, 1100) };
        CompleteRefresh(panel);
        SelectTab(panel, "Settings");
        var authentication = Named<ContentSizedComboBox>(panel, "MCP authentication mode");
        _ = authentication.Handle;
        panel.Font = font;
        panel.PerformLayout();
        for (var index = 0; index < authentication.Items.Count; index++)
        {
            authentication.SelectedIndex = index;
            CompleteRefresh(panel);
            Assert.Equal(index, authentication.SelectedIndex);
            AssertNativeChoicesFit(authentication);
            Assert.True(authentication.Parent!.ClientRectangle.Contains(authentication.Bounds));
            var width = authentication.Width;
            authentication.FitChoices();
            Assert.Equal(width, authentication.Width);
        }
        SaveLayoutSnapshot(Named<TabPage>(panel, "Settings"), $"authentication-{scale}");
    });
}
