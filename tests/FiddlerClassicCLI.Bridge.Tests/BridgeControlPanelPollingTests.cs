// Checks coalesced reads and serialized user intent without live IPC, clipboard access, or modal dialogs.
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PollsKeepActionsEnabledAndApplyWaitsWithCapturedSettings(bool failedRead) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var port = Named<NumericUpDown>(panel, "Listener port");
        port.Value = 9100;
        var pending = client.PendingService = new TaskCompletionSource<HttpServiceStatus>();
        var refresh = panel.RefreshForTestingAsync();
        var reads = client.ServiceStatusCount;
        Assert.Same(refresh, panel.RefreshForTestingAsync());
        Assert.Equal(reads, client.ServiceStatusCount);
        Assert.True(Button(panel, "Apply").Enabled);
        Assert.True(Button(panel, "Authorize client").Enabled);
        Assert.True(Button(panel, "Rotate default token").Enabled);
        Assert.False(Button(panel, "Refresh").Enabled);
        Button(panel, "Apply").PerformClick();
        Assert.False(Button(panel, "Apply").Enabled);
        Assert.False(Button(panel, "Authorize client").Enabled);
        Assert.True(Named<Button>(panel, "Copy loopback endpoint").Enabled);
        Assert.Equal(0, client.ConfigureCount);
        port.Value = 9200;
        Button(panel, "Apply").PerformClick();
        PumpUntilCompleted(panel.RefreshForTestingAsync());
        Assert.Equal(reads, client.ServiceStatusCount);
        client.PendingService = null;
        if (failedRead) pending.SetException(new TimeoutException("Synthetic read timeout"));
        else pending.SetResult(client.Service);
        PumpUntilIdle(panel);
        Assert.Equal(1, client.ConfigureCount);
        Assert.Equal(9100, client.Service.Port);
        Assert.Equal(9200, port.Value);
        Assert.True(Button(panel, "Apply").Enabled);
        Assert.Empty(panel.ServiceErrorText);
        Assert.Equal(reads + 2, client.ServiceStatusCount); // Fresh restart state, then the resulting panel snapshot.
    });

    [Fact]
    public void QueuedEnableDoesNotTurnIntoDisableWhenThePollReportsAnotherClientsChange() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var pending = client.PendingService = new TaskCompletionSource<HttpServiceStatus>();
        var refresh = panel.RefreshForTestingAsync();
        var enabled = Named<CheckBox>(panel, "Enable MCP HTTP service");
        Assert.True(enabled.Enabled);
        enabled.Checked = true;
        Assert.Equal(0, client.EnableCount);
        client.PendingService = null;
        pending.SetResult(new HttpServiceStatus { Enabled = true });
        PumpUntilIdle(panel);
        Assert.True(refresh.IsCompleted);
        Assert.Equal(1, client.EnableCount);
        Assert.Equal(0, client.DisableCount);
    });

    [Fact]
    public void DisposalDropsTheActionWaitingBehindARead() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        client.PendingService = new TaskCompletionSource<HttpServiceStatus>();
        var refresh = panel.RefreshForTestingAsync();
        Button(panel, "Apply").PerformClick();
        panel.Dispose();
        client.PendingService.SetResult(client.Service);
        PumpUntilCompleted(refresh);
        PumpUntilIdle(panel);
        Assert.Equal(0, client.ConfigureCount);
    });
}
