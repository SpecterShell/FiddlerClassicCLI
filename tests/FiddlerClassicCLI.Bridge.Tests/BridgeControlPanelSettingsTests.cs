// Exercises native subtabs, captured checkbox intent, interface choices, and security confirmations.
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
    public void SubtabsKeepSettingsAndInterfaceControlsReadableAtNarrowWidths(float scale) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * scale);
        var client = new FakeHostControlClient { Service = new HttpServiceStatus
        {
            BindMode = HttpBindModes.Selected, BindAddresses = new[] { "127.0.0.1", "10.0.0.1" },
            LoopbackEndpoint = "http://127.0.0.1:8877/mcp",
            AvailableInterfaces = new[] { new HttpInterfaceAddressDto { Address = "127.0.0.1", AdapterName = "Loopback" },
                new HttpInterfaceAddressDto { Address = "10.0.0.1", AdapterName = "Ethernet adapter" } }
        } };
        using var panel = new BridgeControlPanel(client) { Font = font };
        CompleteRefresh(panel);
        foreach (var width in new[] { 900, 456, 320 })
        {
            panel.Size = new Size(width, 1100);
            LayoutAllTabs(panel);
            AssertContentFits(panel);
            Assert.Equal(HttpBindModes.Selected, Named<ComboBox>(panel, "Listener bind mode").SelectedItem);
            Assert.Equal(HttpStartupModes.LastState, Named<ComboBox>(panel, "MCP startup mode").SelectedItem);
            Assert.Equal(8877, Named<NumericUpDown>(panel, "Listener port").Value);
            foreach (var control in Descendants(panel).Where(control => control is ComboBox or CheckBox or CheckedListBox))
            {
                Assert.True(control.Parent!.ClientRectangle.Contains(control.Bounds),
                    $"{control.AccessibleName} at {width}/{scale}: {control.Bounds} / {control.Parent.ClientRectangle}");
                if (control is CheckBox) Assert.True(control.Height >= control.GetPreferredSize(new Size(control.Width, 0)).Height);
                if (control is ComboBox choice) AssertNativeChoicesFit(choice);
            }
            foreach (var name in new[] { "MCP", "Named pipes", "Settings" })
            {
                SelectTab(panel, name);
                SaveLayoutSnapshot(panel, $"tabs-{name.Replace(' ', '-')}-{scale}-{width}");
            }
        }
    });

    [Fact]
    public void NativeSubtabsSeparateManagementDiagnosticsAndSettings() => RunOnSta(() =>
    {
        using var panel = new BridgeControlPanel(new FakeHostControlClient()) { Size = new Size(640, 900) };
        CompleteRefresh(panel);
        var tabs = Assert.Single(panel.Controls.OfType<TabControl>());
        Assert.Equal(new[] { "MCP", "Named pipes", "Settings" }, tabs.TabPages.Cast<TabPage>().Select(page => page.Text));
        Assert.DoesNotContain(Descendants(panel).OfType<GroupBox>(), group => group.Text == "MCP HTTP service");
        var service = Named<TableLayoutPanel>(panel, "MCP service controls");
        var enabled = Named<CheckBox>(panel, "Enable MCP HTTP service");
        Assert.Equal(0, service.GetRow(enabled));
        Assert.DoesNotContain(Descendants(panel).OfType<Label>(), label => label.AccessibleName is "Status" or "Service status");
        var addresses = Named<GroupBox>(panel, "MCP addresses");
        Assert.True(addresses.Top >= service.Bottom);
        Assert.Contains(Named<SelectableAddress>(panel, "Loopback endpoint"), Descendants(addresses));
        Assert.DoesNotContain(Descendants(panel).OfType<Button>(), button => button.Text is "&Enable" or "&Disable");
        SelectTab(panel, "Settings");
        var settings = tabs.SelectedTab!;
        var startup = Named<ComboBox>(settings, "MCP startup mode");
        var authentication = Named<ComboBox>(settings, "MCP authentication mode");
        var preferences = Assert.IsType<TableLayoutPanel>(startup.Parent);
        var authenticationLabel = Named<Label>(settings, "Authentication");
        Assert.Same(authenticationLabel.Parent, authentication.Parent);
        Assert.True(authentication.Left >= authenticationLabel.Right);
        Assert.InRange(Math.Abs(authenticationLabel.Top + authenticationLabel.Height / 2
            - authentication.Top - authentication.Height / 2), 0, 1);
        Assert.Equal(preferences.GetRow(startup) + 1, preferences.GetRow(Named<Label>(settings, "Startup instructions")));
        Assert.Equal(preferences.GetRow(authentication.Parent!) + 1, preferences.GetRow(Named<Label>(settings, "Authentication instructions")));
        Assert.NotNull(Named<GroupBox>(settings, "Versions"));
        var links = Descendants(Named<GroupBox>(settings, "Documentation")).OfType<LinkLabel>().ToArray();
        Assert.Equal(new[] { "Project", "Documentation" }, links.Select(link => link.Text));
        Assert.Equal("https://github.com/SpecterShell/FiddlerClassicCLI", links[0].AccessibleDescription);
        Assert.All(links, link => Assert.StartsWith("https://github.com/SpecterShell/FiddlerClassicCLI", link.AccessibleDescription));
        Assert.Equal(1, authentication.SelectedIndex);
        Assert.Equal(ComboBoxStyle.DropDownList, authentication.DropDownStyle);
        Assert.Equal(new[] { "Require for all", "Non-loopback only", "No authentication" },
            authentication.Items.Cast<object>().Select(item => item.ToString()));
        Assert.Contains("Non-loopback only is the default", Named<Label>(settings, "Authentication instructions").Text);
        Assert.Equal(HttpStartupModes.LastState, Named<ComboBox>(panel, "MCP startup mode").SelectedItem);
    });

    [Theory]
    [InlineData(HttpBindModes.Loopback, HttpAuthenticationModes.Required, false)]
    [InlineData(HttpBindModes.All, HttpAuthenticationModes.Required, true)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.Required, true)]
    [InlineData(HttpBindModes.Loopback, HttpAuthenticationModes.NonLoopback, false)]
    [InlineData(HttpBindModes.All, HttpAuthenticationModes.NonLoopback, true)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.NonLoopback, true)]
    [InlineData(HttpBindModes.Loopback, HttpAuthenticationModes.None, true)]
    [InlineData(HttpBindModes.All, HttpAuthenticationModes.None, true)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.None, true)]
    public void EnablingAcknowledgesRemoteAndUnauthenticatedAccess(string mode, string authenticationMode, bool expectsWarning) => RunOnSta(() =>
    {
        var prompts = new List<string>();
        var client = new FakeHostControlClient { Service = new HttpServiceStatus
        {
            BindMode = mode, AuthenticationMode = authenticationMode, BindAddresses = new[] { "192.168.1.8" }
        } };
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { prompts.Add(message); return true; });
        CompleteRefresh(panel);
        var enabled = Named<CheckBox>(panel, "Enable MCP HTTP service");
        enabled.Checked = true;
        PumpUntilIdle(panel);
        Assert.True(enabled.Checked);
        Assert.Equal(1, client.EnableCount);
        Assert.Equal(expectsWarning, client.LastConfirmation);
        Assert.Equal(expectsWarning ? 1 : 0, prompts.Count);
        if (authenticationMode == HttpAuthenticationModes.None) Assert.Contains("full access", Assert.Single(prompts));
        else if (expectsWarning) Assert.Contains("without encryption", Assert.Single(prompts));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledCheckboxActionsRestoreAuthoritativeState(bool initiallyEnabled) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus
        {
            Enabled = initiallyEnabled, BindMode = HttpBindModes.All, ActiveConnectionCount = 1
        } };
        var prompts = new List<string>();
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { prompts.Add(message); return false; });
        CompleteRefresh(panel);
        var enabled = Named<CheckBox>(panel, "Enable MCP HTTP service");
        enabled.Checked = !initiallyEnabled;
        PumpUntilIdle(panel);
        Assert.Equal(initiallyEnabled, enabled.Checked);
        Assert.Equal(0, client.EnableCount + client.DisableCount);
        Assert.Single(prompts);
        if (initiallyEnabled) Assert.Contains("already dispatched", prompts[0]);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCheckboxActionsRestoreAuthoritativeStateAndReportError(bool initiallyEnabled) => RunOnSta(() =>
    {
        var errors = new List<string>();
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { Enabled = initiallyEnabled },
            MutationFailure = new IOException("Synthetic control failure") };
        using var panel = new BridgeControlPanel(client, showError: errors.Add);
        CompleteRefresh(panel);
        var enabled = Named<CheckBox>(panel, "Enable MCP HTTP service");
        enabled.Checked = !initiallyEnabled;
        PumpUntilIdle(panel);
        Assert.Equal(initiallyEnabled, enabled.Checked);
        Assert.Equal("Synthetic control failure", Assert.Single(errors));
        Assert.Equal("Synthetic control failure", panel.ServiceErrorText);
    });

    [Theory]
    [InlineData("remote")]
    [InlineData("anonymous")]
    [InlineData("connections")]
    [InlineData("startup")]
    public void StaleSafeSnapshotNeverConfirmsANewlyRiskyDaemonState(string changed) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { Enabled = changed == "connections" } };
        var errors = new List<string>();
        var warnings = new List<string>();
        using var panel = new BridgeControlPanel(client, showError: errors.Add,
            confirm: (message, _) => { warnings.Add(message); return true; });
        CompleteRefresh(panel);
        client.BeforeMutation = () => client.Service = new HttpServiceStatus
        {
            Enabled = changed == "connections", ActiveConnectionCount = changed == "connections" ? 1 : 0,
            BindMode = changed is "remote" or "startup" ? HttpBindModes.All : HttpBindModes.Loopback,
            AuthenticationMode = changed == "anonymous" ? HttpAuthenticationModes.None : HttpAuthenticationModes.NonLoopback
        };
        if (changed == "startup")
        {
            SelectTab(panel, "Settings");
            Named<ComboBox>(panel, "MCP startup mode").SelectedItem = HttpStartupModes.Enabled;
            Button(panel, "Save settings").PerformClick();
            PumpUntilIdle(panel);
            Assert.False(client.LastConfiguration!.Confirm);
            Assert.Equal(HttpStartupModes.LastState, client.Service.StartupMode);
        }
        else
        {
            Named<CheckBox>(panel, "Enable MCP HTTP service").Checked = changed != "connections";
            PumpUntilIdle(panel);
            Assert.False(client.LastConfirmation);
        }
        Assert.Empty(warnings);
        Assert.Equal("confirmation_required", Assert.Single(errors));
        Assert.Equal(changed == "connections", client.Service.Enabled);
        Assert.Equal(changed == "connections", Named<CheckBox>(panel, "Enable MCP HTTP service").Checked);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthenticationRemovalRequiresConfirmationAndPreservesIndependentEdits(bool approve) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        var prompts = new List<string>();
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { prompts.Add(message); return approve; });
        CompleteRefresh(panel);
        Named<NumericUpDown>(panel, "Listener port").Value = 9500;
        SelectTab(panel, "Settings");
        Named<ComboBox>(panel, "MCP startup mode").SelectedItem = HttpStartupModes.Disabled;
        var authentication = Named<ComboBox>(panel, "MCP authentication mode");
        authentication.SelectedIndex = 2;
        CompleteRefresh(panel);
        Assert.Equal(2, authentication.SelectedIndex);
        Button(panel, "Save settings").PerformClick();
        PumpUntilIdle(panel);
        Assert.Contains("full access", Assert.Single(prompts));
        Assert.Equal(approve ? 1 : 0, client.ConfigureCount);
        Assert.Equal(approve ? HttpAuthenticationModes.None : HttpAuthenticationModes.NonLoopback, client.Service.AuthenticationMode);
        Assert.Equal(9500, Named<NumericUpDown>(panel, "Listener port").Value);
        Assert.False(Named<CheckBox>(panel, "Enable MCP HTTP service").Enabled);
        if (approve)
        {
            Assert.True(client.LastConfiguration!.Confirm);
            Assert.Null(client.LastConfiguration.BindMode);
            Assert.Null(client.LastConfiguration.Port);
            Assert.False(Button(panel, "Authorize client").Enabled);
            Assert.False(Button(panel, "Deauthorize").Enabled);
            Assert.False(Button(panel, "Rotate default token").Enabled);
            Assert.Contains("Authentication is disabled", Named<Label>(panel, "Authentication warning").Text);
            Assert.Equal(0, client.CredentialActionCount);
        }
    });

    [Fact]
    public void FailedPeerRefreshDisablesSettingsAndKeepsPendingEdits() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        SelectTab(panel, "Settings");
        var authentication = Named<ComboBox>(panel, "MCP authentication mode");
        authentication.SelectedIndex = 2;
        Assert.True(Button(panel, "Save settings").Enabled);
        client.ServiceFailure = new InvalidOperationException("The daemon does not support the current MCP HTTP settings.");
        CompleteRefresh(panel);
        Assert.False(Button(panel, "Save settings").Enabled);
        Assert.False(panel.ServiceSettingsEnabled);
        Assert.False(Named<CheckBox>(panel, "Enable MCP HTTP service").Enabled);
        Assert.Equal(2, authentication.SelectedIndex);
        Assert.Contains("does not support", panel.ServiceErrorText);
        client.ServiceFailure = null;
        CompleteRefresh(panel);
        Assert.True(Button(panel, "Save settings").Enabled);
        Assert.Equal(2, authentication.SelectedIndex);
    });

    [Fact]
    public void StartupAndBindingStayEditableWhileRunningButAuthenticationDoesNot() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { Enabled = true } };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        SelectTab(panel, "Settings");
        Named<ComboBox>(panel, "MCP startup mode").SelectedItem = HttpStartupModes.Disabled;
        Assert.False(Named<ComboBox>(panel, "MCP authentication mode").Enabled);
        Assert.True(panel.ServiceSettingsEnabled);
        Assert.True(Button(panel, "Save settings").Enabled);
        Button(panel, "Save settings").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal(HttpStartupModes.Disabled, client.LastConfiguration!.StartupMode);
        Assert.Null(client.LastConfiguration.AuthenticationMode);
        Assert.True(client.Service.Enabled);
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DesiredOrActualActiveStateStaysCheckedAndCanBeDisabled(bool enabled, bool running) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { Enabled = enabled, Running = running } };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var checkbox = Named<CheckBox>(panel, "Enable MCP HTTP service");
        Assert.True(checkbox.Checked);
        Assert.True(checkbox.Enabled);
        Assert.True(panel.ServiceSettingsEnabled);
        Assert.False(Named<ComboBox>(panel, "MCP authentication mode").Enabled);
        checkbox.Checked = false;
        PumpUntilIdle(panel);
        Assert.Equal(1, client.DisableCount);
        Assert.False(checkbox.Checked);
        Assert.False(client.Service.Enabled);
        Assert.False(client.Service.Running);
        Assert.True(panel.ServiceSettingsEnabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupOnlySavePreservesPendingAuthenticationWhenAnotherClientEnables(bool savedDisabled) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        SelectTab(panel, "Settings");
        var authentication = Named<ComboBox>(panel, "MCP authentication mode");
        authentication.SelectedIndex = 2;
        Named<ComboBox>(panel, "MCP startup mode").SelectedItem = HttpStartupModes.Disabled;
        client.Service.Enabled = !savedDisabled;
        client.Service.Running = true;
        CompleteRefresh(panel);
        Assert.Equal(2, authentication.SelectedIndex);
        Assert.False(authentication.Enabled);
        Button(panel, "Save settings").PerformClick();
        PumpUntilIdle(panel);
        Assert.Null(client.LastConfiguration!.AuthenticationMode);
        Assert.Equal(HttpAuthenticationModes.NonLoopback, client.Service.AuthenticationMode);
        Assert.Equal(2, authentication.SelectedIndex);
        Assert.Equal(!savedDisabled, client.Service.Enabled);
        Assert.True(client.Service.Running);
    });

    [Fact]
    public void QueuedSettingsSaveUsesClickTimeValuesAndRetainsNewerEdits() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        var prompts = new List<string>();
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { prompts.Add(message); return true; });
        CompleteRefresh(panel);
        SelectTab(panel, "Settings");
        var startup = Named<ComboBox>(panel, "MCP startup mode");
        var authentication = Named<ComboBox>(panel, "MCP authentication mode");
        startup.SelectedItem = HttpStartupModes.Disabled;
        authentication.SelectedIndex = 2;
        var pending = client.PendingService = new TaskCompletionSource<HttpServiceStatus>();
        var refresh = panel.RefreshForTestingAsync();
        Button(panel, "Save settings").PerformClick();
        Assert.Equal(0, client.ConfigureCount);
        startup.SelectedItem = HttpStartupModes.LastState;
        authentication.SelectedIndex = 0;
        client.PendingService = null;
        pending.SetResult(client.Service);
        PumpUntilIdle(panel);
        Assert.True(refresh.IsCompleted);
        Assert.Equal(HttpStartupModes.Disabled, client.LastConfiguration!.StartupMode);
        Assert.Equal(HttpAuthenticationModes.None, client.LastConfiguration.AuthenticationMode);
        Assert.True(client.LastConfiguration.Confirm);
        Assert.Single(prompts);
        Assert.Equal(HttpStartupModes.LastState, startup.SelectedItem);
        Assert.Equal(0, authentication.SelectedIndex);
        Assert.True(Button(panel, "Save settings").Enabled);
    });

    [Fact]
    public void ExplicitAuthenticationOffRequestRequiresWarningEvenWhenAlreadyOff() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { AuthenticationMode = HttpAuthenticationModes.None } };
        var warnings = new List<string>();
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { warnings.Add(message); return true; });
        CompleteRefresh(panel);
        SelectTab(panel, "Settings");
        var authentication = Named<ComboBox>(panel, "MCP authentication mode");
        authentication.SelectedIndex = 0;
        authentication.SelectedIndex = 2;
        Button(panel, "Save settings").PerformClick();
        PumpUntilIdle(panel);
        Assert.Contains("full access", Assert.Single(warnings));
        Assert.Equal(HttpAuthenticationModes.None, client.LastConfiguration!.AuthenticationMode);
        Assert.True(client.LastConfiguration.Confirm);
    });

    [Fact]
    public void RemoteStartupRequiresAcknowledgmentWhenSavingEitherTab() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = AllInterfaceStatus() };
        var prompts = new List<string>();
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { prompts.Add(message); return true; });
        CompleteRefresh(panel);
        SelectTab(panel, "Settings");
        Named<ComboBox>(panel, "MCP startup mode").SelectedItem = HttpStartupModes.Enabled;
        Button(panel, "Save settings").PerformClick();
        PumpUntilIdle(panel);
        Assert.True(client.LastConfiguration!.Confirm);
        SelectTab(panel, "MCP");
        Named<NumericUpDown>(panel, "Listener port").Value = 9500;
        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal(2, prompts.Count);
        Assert.All(prompts, message => Assert.Contains("without encryption", message));
        Assert.Equal(2, client.ConfigureCount);
    });

    [Fact]
    public void InterfaceChecksSurviveAdapterChangesAndConfigureMultipleExactAddresses() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus
        {
            BindMode = HttpBindModes.Selected, BindAddresses = new[] { "10.0.0.1" }, LoopbackEndpoint = string.Empty,
            AvailableInterfaces = new[] { new HttpInterfaceAddressDto { Address = "10.0.0.1", AdapterName = "Ethernet" },
                new HttpInterfaceAddressDto { Address = "10.0.0.2", AdapterName = "Wi-Fi" } }
        } };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var list = Named<InterfaceAddressList>(panel, "Selected IPv4 interfaces");
        Assert.True(list.Visible);
        Assert.Empty(Named<SelectableAddress>(panel, "Loopback endpoint").Text);
        Assert.False(Named<SelectableAddress>(panel, "Loopback endpoint").Parent!.Visible);
        Assert.False(Named<Label>(panel, "Loopback").Visible);
        Assert.False(Named<Button>(panel, "Copy loopback endpoint").Enabled);
        list.SetItemChecked(1, true);
        client.Service.AvailableInterfaces = client.Service.AvailableInterfaces.Skip(1).ToArray();
        CompleteRefresh(panel);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, list.SelectedAddresses.OrderBy(value => value));
        Assert.Contains(list.Items.Cast<object>(), value => value.ToString()!.Contains("unavailable"));
        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal(HttpBindModes.Selected, client.LastConfiguration!.BindMode);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, client.LastConfiguration.BindAddresses!.OrderBy(value => value));
    });

    [Fact]
    public void InterfaceSelectionIsBoundedAndSelectedUrlsRetainAllSixteenEndpoints() => RunOnSta(() =>
    {
        using var list = new InterfaceAddressList();
        var available = Enumerable.Range(1, 70).Select(index => new HttpInterfaceAddressDto { Address = $"10.0.0.{index}" }).ToArray();
        list.UpdateAddresses(available, Array.Empty<string>());
        Assert.Equal(HttpListenerLimits.MaximumAvailableInterfaces, list.Items.Count);
        for (var index = 0; index < 20; index++) list.SetItemChecked(index, true);
        Assert.Equal(HttpListenerLimits.MaximumSelectedAddresses, list.SelectedAddresses.Length);
        using var endpoints = new LanEndpointList();
        endpoints.UpdateEndpoints(new HttpServiceStatus { BindMode = HttpBindModes.Selected,
            LanEndpoints = list.SelectedAddresses.Select(address => $"http://{address}:8877/mcp").ToArray() });
        Assert.Equal(HttpListenerLimits.MaximumSelectedAddresses, Descendants(endpoints).OfType<SelectableAddress>().Count());
    });
}
