// Verifies extension-owned startup without a visible tab and version refresh after host replacement.
using System.Windows.Forms;
using FiddlerClassic.Bridge;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Fact]
    public void StartsTheHostWithoutCreatingOrSelectingTheTab() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var lifetime = new BridgeHostLifetime(client);
        PumpUntilCompleted(lifetime.Completion);
        Assert.Equal(1, client.StartCount);
        using var tabs = new TabControl();
        tabs.TabPages.Add(new TabPage("Existing tab"));
        var management = new TabPage("Fiddler Classic CLI");
        using var panel = new BridgeControlPanel(client, lifetime, "6.x-test");
        management.Controls.Add(panel);
        tabs.TabPages.Add(management);
        tabs.SelectedIndex = 0;
        Assert.Same(tabs.TabPages[0], tabs.SelectedTab);
        Assert.False(panel.IsHandleCreated);
        Assert.Equal(1, client.StartCount);
        CompleteRefresh(panel);
        CompleteRefresh(panel);
        Assert.Equal(1, client.StartCount);
        Assert.Contains("Fiddler Classic: 6.x-test", panel.VersionText);
    });

    [Fact]
    public void RetainsStartupFailureWithoutAnUnobservedFault() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Failure = new InvalidOperationException("Install the host and retry.") };
        using var lifetime = new BridgeHostLifetime(client);
        PumpUntilCompleted(lifetime.Completion);
        Assert.Equal("Install the host and retry.", lifetime.Error);
        Assert.Equal(TaskStatus.RanToCompletion, lifetime.Completion.Status);
    });

    [Fact]
    public void UnloadCancelsStartupWithoutOwningTheDaemonProcess() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { WaitForCancellation = true };
        var lifetime = new BridgeHostLifetime(client);
        lifetime.Dispose();
        PumpUntilCompleted(lifetime.Completion);
        Assert.Null(lifetime.Error);
    });

    [Fact]
    public void RefreshesConfiguredAndRunningVersionsAfterRestart() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { LaunchRecord = new HostLaunchRecord { HostVersion = "host-v1" } };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        Assert.Contains("Running daemon: test-v1", panel.VersionText);
        Assert.Contains("Configured host: host-v1", panel.VersionText);
        client.Daemon = new DaemonStatus { HostVersion = "test-v2" };
        client.LaunchRecord = new HostLaunchRecord { HostVersion = "host-v2" };
        CompleteRefresh(panel);
        Assert.Contains("Running daemon: test-v2", panel.VersionText);
        Assert.Contains("Configured host: host-v2", panel.VersionText);
        Assert.DoesNotContain("v1", panel.VersionText);
    });
}
