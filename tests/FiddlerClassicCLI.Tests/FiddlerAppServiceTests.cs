// Tests lifecycle ordering, evidence-safety boundaries, idempotence, and stable failures using inert processes.
using System.ComponentModel;
using System.Diagnostics;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class FiddlerAppServiceTests
{
    [Fact]
    public void DetectDoesNotAcquireMutationLeaseOrStartAnything()
    {
        var platform = new FakeFiddlerAppPlatform();
        var process = new FakeFiddlerAppProcess();
        platform.Processes.Add(process);
        var result = new FiddlerAppService(platform).Detect();
        Assert.Single(result.Installations);
        Assert.Equal(42, Assert.Single(result.Processes).ProcessId);
        Assert.Equal(0, platform.Acquisitions);
        Assert.Equal(0, platform.Starts);
        Assert.Equal(0, process.CloseRequests);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void OpenUsesSupportedInstallationAndReleasesOwnership()
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Installations.Insert(0, new(@"C:\Old\Fiddler.exe", "4.0.0.0", false));
        var result = new FiddlerAppService(platform).Open(null, TestContext.Current.CancellationToken);
        Assert.True(result.Changed);
        Assert.True(result.Running);
        Assert.Equal(1234, result.ProcessId);
        Assert.Equal(FakeFiddlerAppPlatform.DefaultPath, platform.StartedPath);
        Assert.False(platform.Locked);
    }

    [Fact]
    public void OpenDoesNotDuplicateAnExistingProcessOrChangeItsCaptureState()
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Processes.Add(new());
        var result = new FiddlerAppService(platform).Open(null, TestContext.Current.CancellationToken);
        Assert.False(result.Changed);
        Assert.Equal(42, result.ProcessId);
        Assert.Equal(0, platform.Starts);
        Assert.Equal(0, platform.Processes[0].CloseRequests);
        Assert.True(platform.Processes[0].Disposed);
    }

    [Fact]
    public void OpenRejectsADifferentRequestedInstallation()
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Processes.Add(new());
        const string other = @"C:\Other\Fiddler.exe";
        platform.Installations.Add(new(other, "5.0.0.0", true));
        var exception = Assert.Throws<BridgeClientException>(() => new FiddlerAppService(platform).Open(other, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.Conflict, exception.Code);
        Assert.Equal(0, platform.Starts);
        Assert.False(platform.Locked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseAndRestartRequireConfirmationBeforeAnyProcessAccess(bool restart)
    {
        var platform = new FakeFiddlerAppPlatform();
        var exception = await Assert.ThrowsAsync<BridgeClientException>(() =>
            new FiddlerAppService(platform).CloseAsync(restart, null, null, 10, false, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.ConfirmationRequired, exception.Code);
        Assert.Equal(0, platform.Acquisitions);
    }

    [Fact]
    public async Task CloseIsIdempotentWhenAlreadyStopped()
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Installations.Clear();
        var result = await new FiddlerAppService(platform).CloseAsync(false, null, null, 10, true, TestContext.Current.CancellationToken);
        Assert.False(result.Changed);
        Assert.False(result.Running);
        Assert.Equal(0, platform.Starts);
    }

    [Fact]
    public async Task RestartWaitsForExitAndUsesTheRunningExecutable()
    {
        var platform = new FakeFiddlerAppPlatform();
        const string selected = @"D:\Custom\Fiddler.exe";
        platform.Installations.Add(new(selected, "5.0.0.0", true));
        var process = new FakeFiddlerAppProcess(path: selected)
        {
            OnClose = () => platform.Events.Add("close"),
            Wait = _ => { Assert.True(platform.Locked); platform.Events.Add("exit"); return Task.CompletedTask; }
        };
        platform.Processes.Add(process);
        var result = await new FiddlerAppService(platform).CloseAsync(true, null, null, 10, true, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "close", "exit", "start" }, platform.Events);
        Assert.Equal(selected, platform.StartedPath);
        Assert.Equal("restart", result.Action);
        Assert.True(process.Disposed);
        Assert.False(platform.Locked);
    }

    [Fact]
    public async Task RestartStartsADetectedInstallationWhenStopped()
    {
        var platform = new FakeFiddlerAppPlatform();
        var result = await new FiddlerAppService(platform).CloseAsync(true, null, null, 10, true, TestContext.Current.CancellationToken);
        Assert.True(result.Changed);
        Assert.Equal(1, platform.Starts);
    }

    [Fact]
    public async Task RestartValidatesExecutableBeforeClosing()
    {
        var platform = new FakeFiddlerAppPlatform();
        var process = new FakeFiddlerAppProcess(path: @"D:\Missing\Fiddler.exe");
        platform.Processes.Add(process);
        var exception = await Assert.ThrowsAsync<BridgeClientException>(() =>
            new FiddlerAppService(platform).CloseAsync(true, null, null, 10, true, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.Unavailable, exception.Code);
        Assert.Equal(0, process.CloseRequests);
        Assert.Equal(0, platform.Starts);
    }

    [Fact]
    public async Task ModalDialogDoesNotPermitForcedCloseOrRestart()
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Processes.Add(new() { AcceptClose = false });
        var exception = await Assert.ThrowsAsync<BridgeClientException>(() =>
            new FiddlerAppService(platform).CloseAsync(true, null, null, 10, true, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.Conflict, exception.Code);
        Assert.Equal(0, platform.Starts);
        Assert.False(platform.Locked);
    }

    [Fact]
    public async Task TimeoutDoesNotLaunchReplacement()
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Processes.Add(new() { Wait = token => Task.Delay(Timeout.Infinite, token) });
        var exception = await Assert.ThrowsAsync<BridgeClientException>(() =>
            new FiddlerAppService(platform).CloseAsync(true, null, null, 1, true, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.Timeout, exception.Code);
        Assert.Equal(0, platform.Starts);
        Assert.True(platform.Processes[0].Disposed);
        Assert.False(platform.Locked);
    }

    [Fact]
    public async Task CancellationAfterCloseDoesNotLaunchReplacement()
    {
        var platform = new FakeFiddlerAppPlatform();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        platform.Processes.Add(new() { OnClose = cancellation.Cancel });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FiddlerAppService(platform).CloseAsync(true, null, null, 10, true, cancellation.Token));
        Assert.Equal(0, platform.Starts);
        Assert.False(platform.Locked);
    }

    [Fact]
    public void PreCancelledOpenNeverAcquiresOwnershipOrLaunches()
    {
        var platform = new FakeFiddlerAppPlatform();
        Assert.ThrowsAny<OperationCanceledException>(() => new FiddlerAppService(platform).Open(null, new(true)));
        Assert.Equal(0, platform.Acquisitions);
    }

    [Fact]
    public async Task MultipleProcessesRequireSelectionAndCloseOnlyTheSelectedPid()
    {
        var platform = new FakeFiddlerAppPlatform();
        var first = new FakeFiddlerAppProcess(10);
        var second = new FakeFiddlerAppProcess(20);
        platform.Processes.AddRange([first, second]);
        var service = new FiddlerAppService(platform);
        var exception = await Assert.ThrowsAsync<BridgeClientException>(() => service.CloseAsync(false, null, null, 10, true, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.Conflict, exception.Code);
        Assert.Equal(0, first.CloseRequests);
        Assert.Equal(0, second.CloseRequests);
        var result = await service.CloseAsync(false, 20, null, 10, true, TestContext.Current.CancellationToken);
        Assert.Equal(20, result.ProcessId);
        Assert.Equal(0, first.CloseRequests);
        Assert.Equal(1, second.CloseRequests);
    }

    [Fact]
    public async Task UnknownPidDoesNotCloseOrStartAnyProcess()
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Processes.Add(new());
        var exception = await Assert.ThrowsAsync<BridgeClientException>(() =>
            new FiddlerAppService(platform).CloseAsync(true, 99, null, 10, true, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.NotFound, exception.Code);
        Assert.Equal(0, platform.Starts);
        Assert.Equal(0, platform.Processes[0].CloseRequests);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(61, null)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public async Task RejectsInvalidBoundsBeforeTakingOwnership(int seconds, int? pid)
    {
        var platform = new FakeFiddlerAppPlatform();
        var exception = await Assert.ThrowsAsync<BridgeClientException>(() =>
            new FiddlerAppService(platform).CloseAsync(false, pid, null, seconds, true, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidRequest, exception.Code);
        Assert.Equal(0, platform.Acquisitions);
    }

    [Fact]
    public void MissingUnsupportedAndMalformedInstallationsHaveStableErrors()
    {
        var platform = new FakeFiddlerAppPlatform();
        var service = new FiddlerAppService(platform);
        Assert.Equal(ErrorCodes.InvalidRequest, Assert.Throws<BridgeClientException>(() => service.Open("relative.exe", TestContext.Current.CancellationToken)).Code);
        platform.Installations.Clear();
        Assert.Equal(ErrorCodes.Unavailable, Assert.Throws<BridgeClientException>(() => service.Open(null, TestContext.Current.CancellationToken)).Code);
        platform.Installations.Add(new(FakeFiddlerAppPlatform.DefaultPath, "4.0.0.0", false));
        Assert.Equal(ErrorCodes.InvalidRequest, Assert.Throws<BridgeClientException>(() => service.Open(null, TestContext.Current.CancellationToken)).Code);
        Assert.Equal(0, platform.Starts);
        Assert.False(platform.Locked);
    }

    [Fact]
    public void LaunchFailureMapsToUnavailable()
    {
        var platform = new FakeFiddlerAppPlatform { StartError = new Win32Exception(5) };
        var exception = Assert.Throws<BridgeClientException>(() => new FiddlerAppService(platform).Open(null, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.Unavailable, exception.Code);
        Assert.False(platform.Locked);
    }

    [Fact]
    public void NativeLaunchUsesAnExactPathAndOnlyNoAttach()
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = WindowsFiddlerAppPlatform.CreateStartInfo(FakeFiddlerAppPlatform.DefaultPath);
        Assert.Equal(FakeFiddlerAppPlatform.DefaultPath, start.FileName);
        Assert.Equal(new[] { "-noattach" }, start.ArgumentList);
        Assert.True(start.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Normal, start.WindowStyle);
        Assert.Equal(@"C:\Test Fiddler", start.WorkingDirectory);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void NativeOwnershipCheckAndCrossProcessLeaseUseOnlyThisTestProcessAndPipe()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        Assert.True(WindowsFiddlerAppPlatform.IsOwnedByCurrentUser(process));
        Assert.StartsWith($"fiddler-classic-cli.tests.{Environment.ProcessId}.", PipeNames.ForCurrentUser(), StringComparison.Ordinal);
        IFiddlerAppPlatform platform = new WindowsFiddlerAppPlatform();
        using (platform.AcquireOperation())
        {
            Assert.Equal(ErrorCodes.Conflict, Assert.Throws<BridgeClientException>(() => platform.AcquireOperation()).Code);
        }
        using var released = platform.AcquireOperation();
    }
}
