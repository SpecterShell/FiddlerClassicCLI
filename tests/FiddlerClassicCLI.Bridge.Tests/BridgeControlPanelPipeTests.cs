// Checks read-only pipe diagnostics, failure recovery, copied paths, and wrapping on STA threads.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Fact]
    public void PipeDiagnosticsUseActualEndpointsAndRefreshWithoutStartingDaemon() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { PipeName = "custom-daemon", Daemon = RunningDaemon() };
        using var panel = new BridgeControlPanel(client, bridgeStatus: () =>
            new BridgePipeStatus("custom-bridge", BridgeListenerState.Listening));
        Assert.Equal(@"\\.\pipe\custom-bridge", Named<SelectableAddress>(panel, "Bridge pipe path").Text);
        Assert.Equal(@"\\.\pipe\custom-daemon", Named<SelectableAddress>(panel, "Daemon pipe path").Text);
        Assert.Equal(@"\\.\pipe\custom-bridge", Named<Button>(panel, "Copy bridge pipe path").AccessibleDescription);
        Assert.Equal(@"\\.\pipe\custom-daemon", Named<Button>(panel, "Copy daemon pipe path").AccessibleDescription);
        Assert.Equal("Not checked", Named<Label>(panel, "Daemon pipe status").Text);

        SelectTab(panel, "Named pipes");
        var previousDaemonReads = client.DaemonStatusCount;
        var previousServiceReads = client.ServiceStatusCount;
        Named<Button>(panel, "Refresh named pipes").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal("Listening", Named<Label>(panel, "Bridge listener status").Text);
        Assert.Equal("Responding", Named<Label>(panel, "Daemon pipe status").Text);
        Assert.Equal("1234", Named<Label>(panel, "Daemon process ID").Text);
        Assert.DoesNotContain(Descendants(panel).OfType<Label>(), label => label.AccessibleName == "Daemon start time UTC");
        Assert.Equal($"Bridge v{ProtocolConstants.Version}, daemon v{DaemonProtocol.Version}",
            Named<Label>(panel, "Named pipe protocol versions").Text);
        Assert.Equal("Current Windows user only", Named<Label>(panel, "Named pipe access restrictions").Text);
        Assert.Equal(previousDaemonReads + 1, client.DaemonStatusCount);
        Assert.Equal(previousServiceReads, client.ServiceStatusCount);
        Assert.Equal(0, client.StartCount);
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void FailedDaemonCheckClearsStaleDetailsAndRecoversAfterRestart(bool timeout, bool allRequestsFail) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Daemon = RunningDaemon() };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        client.DaemonFailure = timeout ? new TimeoutException("No daemon response.") : new IOException("Pipe closed.");
        if (allRequestsFail) client.Failure = client.DaemonFailure;
        CompleteRefresh(panel);
        Assert.Equal(timeout ? "Timed out (state unknown)" : "Unavailable (state unknown)",
            Named<Label>(panel, "Daemon pipe status").Text);
        Assert.Equal("Unknown", Named<Label>(panel, "Daemon process ID").Text);
        Assert.Equal(client.DaemonFailure.Message, Named<Label>(panel, "Daemon pipe error").Text);
        Assert.Contains("Running daemon: unavailable", panel.VersionText);
        Assert.True(Named<Button>(panel, "Copy daemon pipe path").Enabled);

        client.DaemonFailure = null;
        client.Failure = null;
        client.Daemon = RunningDaemon();
        client.Daemon.ProcessId = 5678;
        client.Daemon.StartedAtUtc = "2026-09-23T12:34:56Z";
        CompleteRefresh(panel);
        Assert.Equal("Responding", Named<Label>(panel, "Daemon pipe status").Text);
        Assert.Equal("5678", Named<Label>(panel, "Daemon process ID").Text);
        Assert.Empty(Named<Label>(panel, "Daemon pipe error").Text);
    });

    [Fact]
    public void HttpFailureDoesNotHidePipeStateAndCopiesRemainAvailableDuringRefresh() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Daemon = RunningDaemon(), ServiceFailure = new IOException("HTTP status failed.") };
        var bridge = new BridgePipeStatus("test-bridge", BridgeListenerState.Retrying, "Pipe creation failed.");
        using var panel = new BridgeControlPanel(client, bridgeStatus: () => bridge);
        CompleteRefresh(panel);
        Assert.Equal("Responding", Named<Label>(panel, "Daemon pipe status").Text);
        Assert.Equal("Retrying", Named<Label>(panel, "Bridge listener status").Text);
        Assert.Equal(bridge.Error, Named<Label>(panel, "Bridge listener error").Text);
        client.ServiceFailure = null;
        client.PendingService = new TaskCompletionSource<HttpServiceStatus>();
        bridge = new BridgePipeStatus("test-bridge", BridgeListenerState.Listening);
        var refresh = panel.RefreshForTestingAsync();
        Assert.True(Named<Button>(panel, "Copy bridge pipe path").Enabled);
        Assert.True(Named<Button>(panel, "Copy daemon pipe path").Enabled);
        Assert.False(Named<Button>(panel, "Refresh named pipes").Enabled);
        Assert.Empty(Named<Label>(panel, "Bridge listener error").Text);
        client.PendingService.SetResult(client.Service);
        PumpUntilCompleted(refresh);
    });

    [Fact]
    public void MissingBridgeDoesNotOfferAnUnconfirmedEndpoint() => RunOnSta(() =>
    {
        using var panel = new BridgeControlPanel(new FakeHostControlClient());
        Assert.Equal("Unavailable", Named<SelectableAddress>(panel, "Bridge pipe path").Text);
        Assert.False(Named<Button>(panel, "Copy bridge pipe path").Enabled);
        Assert.Equal(string.Empty, Named<Button>(panel, "Copy bridge pipe path").AccessibleDescription);
    });

    [Theory]
    [InlineData(8.25f)]
    [InlineData(16f)]
    public void LongPipePathsAndErrorsFitNarrowPanes(float fontSize) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, fontSize);
        var client = new FakeHostControlClient { PipeName = new string('d', 200), Daemon = RunningDaemon() };
        var error = string.Join(" ", Enumerable.Repeat("The listener could not open the pipe.", 6));
        using var panel = new BridgeControlPanel(client, bridgeStatus: () =>
            new BridgePipeStatus(new string('b', 200), BridgeListenerState.Retrying, error)) { Font = font };
        CompleteRefresh(panel);
        foreach (var width in new[] { 456, 320, 900 })
        {
            panel.Size = new Size(width, 1000);
            panel.PerformLayout();
            SelectTab(panel, "Named pipes");
            Application.DoEvents();
            AssertContentFits(panel);
            var group = Named<TableLayoutPanel>(panel, "Named pipes");
            Assert.Empty(Descendants(Named<TabPage>(panel, "Named pipes")).OfType<GroupBox>());
            var rows = Descendants(group).OfType<EndpointRow>().ToArray();
            Assert.Equal(2, rows.Length);
            foreach (var row in rows)
            {
                var label = Assert.Single(row.Controls.OfType<SelectableAddress>());
                var copy = Assert.Single(row.Controls.OfType<ClipboardButton>());
                Assert.InRange(copy.Left - label.Right, 0, 12);
                Assert.True(copy.Top < label.Bottom && label.Top < copy.Bottom);
                Assert.True(row.ClientRectangle.Contains(copy.Bounds));
                Assert.Equal(label.Text, copy.GetCopyText!());
                Assert.NotNull(copy.Image);
            }
            Assert.Contains(group, Descendants(Named<TabPage>(panel, "Named pipes")));
            Assert.DoesNotContain(Named<GroupBox>(panel, "Versions"), Descendants(Named<TabPage>(panel, "Named pipes")));
            SaveLayoutSnapshot(group, $"pipes-{fontSize}-{width}");
        }
    });

    private static DaemonStatus RunningDaemon() => new DaemonStatus
    {
        Running = true, ProcessId = 1234, StartedAtUtc = "2026-09-23T10:00:00Z", HostVersion = "test-v1"
    };
}
