// Verifies staged binding edits and the confirmed restart sequence without changing a real listener.
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindingEditsWaitForApplyAndPreserveEnabledState(bool active) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { Enabled = active, Running = active } };
        var errors = new List<string>();
        using var panel = new BridgeControlPanel(client, showError: errors.Add);
        CompleteRefresh(panel);
        var port = Named<NumericUpDown>(panel, "Listener port");
        Assert.True(panel.ServiceSettingsEnabled);
        port.Value = 9500;
        CompleteRefresh(panel);
        Assert.Empty(client.MutationCalls);
        Assert.Equal(8877, client.Service.Port);
        Assert.Equal(9500, port.Value);
        Assert.Equal("http://127.0.0.1:8877/mcp", Named<SelectableAddress>(panel, "Loopback endpoint").Text);
        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Empty(errors);
        Assert.Equal(active ? new[] { "disable", "configure", "enable" } : new[] { "configure" }, client.MutationCalls);
        Assert.Equal(9500, client.Service.Port);
        Assert.Equal(active, client.Service.Enabled);
        Assert.Equal(active, Named<CheckBox>(panel, "Enable MCP HTTP service").Checked);
        Assert.Equal("http://127.0.0.1:9500/mcp", Named<SelectableAddress>(panel, "Loopback endpoint").Text);
    });

    [Theory]
    [InlineData("remote")]
    [InlineData("anonymous")]
    [InlineData("connections")]
    public void CancellingAnApplyWarningLeavesTheListenerUntouched(string reason) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus
        {
            Enabled = true, Running = true,
            AuthenticationMode = reason == "anonymous" ? HttpAuthenticationModes.None : HttpAuthenticationModes.NonLoopback,
            ActiveConnectionCount = reason == "connections" ? 1 : 0
        } };
        var warnings = new List<string>();
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { warnings.Add(message); return false; });
        CompleteRefresh(panel);
        Named<NumericUpDown>(panel, "Listener port").Value = 9500;
        if (reason == "remote") Named<ComboBox>(panel, "Listener bind mode").SelectedItem = HttpBindModes.All;
        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Empty(client.MutationCalls);
        Assert.True(client.Service.Running);
        Assert.Equal(8877, client.Service.Port);
        Assert.Equal(9500, Named<NumericUpDown>(panel, "Listener port").Value);
        Assert.Contains(reason == "remote" ? "without encryption" : reason == "anonymous" ? "full access" : "disconnect", Assert.Single(warnings));
    });

    [Fact]
    public void ApplyingRemoteBindingConfirmsAccessAndActiveConnectionRestart() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { Enabled = true, Running = true, ActiveConnectionCount = 1 } };
        var warnings = new List<string>();
        using var panel = new BridgeControlPanel(client, confirm: (message, _) => { warnings.Add(message); return true; });
        CompleteRefresh(panel);
        Named<ComboBox>(panel, "Listener bind mode").SelectedItem = HttpBindModes.All;
        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal(new[] { "disable", "configure", "enable" }, client.MutationCalls);
        Assert.Equal(2, warnings.Count);
        Assert.True(client.LastConfirmation);
        Assert.True(client.Service.Enabled);
        Assert.Equal(HttpBindModes.All, client.Service.BindMode);
    });

    [Theory]
    [InlineData("disable")]
    [InlineData("configure")]
    [InlineData("enable")]
    public void FailedApplyStopsTheSequenceAndKeepsEditsAndErrorVisible(string stage) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { Enabled = true, Running = true } };
        client.BeforeMutation = () =>
        {
            if (client.MutationCalls.Last() == stage) throw new IOException("Synthetic " + stage + " failure");
        };
        var errors = new List<string>();
        using var panel = new BridgeControlPanel(client, showError: errors.Add);
        CompleteRefresh(panel);
        Named<NumericUpDown>(panel, "Listener port").Value = 9500;
        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal(stage, client.MutationCalls.Last());
        Assert.Equal("Synthetic " + stage + " failure", Assert.Single(errors));
        Assert.Equal(errors[0], panel.ServiceErrorText);
        Assert.Equal(9500, Named<NumericUpDown>(panel, "Listener port").Value);
        Assert.Equal(stage == "disable", Named<CheckBox>(panel, "Enable MCP HTTP service").Checked);
        Assert.Equal(stage == "enable" ? 9500 : 8877, client.Service.Port);
    });

    [Fact]
    public void ClientArrivingAfterApplySnapshotRequiresConfirmationBeforeStop() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { Enabled = true, Running = true } };
        client.BeforeMutation = () => client.Service.ActiveConnectionCount = 1;
        var errors = new List<string>();
        using var panel = new BridgeControlPanel(client, showError: errors.Add);
        CompleteRefresh(panel);
        Named<NumericUpDown>(panel, "Listener port").Value = 9500;
        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal(new[] { "disable" }, client.MutationCalls);
        Assert.Equal("confirmation_required", Assert.Single(errors));
        Assert.True(client.Service.Running);
        Assert.False(client.LastConfirmation);
    });

    [Fact]
    public void EmptySelectionAndUnchangedBindingsNeverStopTheListener() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = new HttpServiceStatus { Enabled = true, Running = true, BindMode = HttpBindModes.All, BindAddresses = new[] { "0.0.0.0" } } };
        var errors = new List<string>();
        using var panel = new BridgeControlPanel(client, showError: errors.Add);
        CompleteRefresh(panel);
        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Empty(client.MutationCalls);
        Named<ComboBox>(panel, "Listener bind mode").SelectedItem = HttpBindModes.Selected;
        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Empty(client.MutationCalls);
        Assert.Contains("at least one", Assert.Single(errors));
        Assert.True(client.Service.Running);
    });
}
