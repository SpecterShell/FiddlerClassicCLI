// Verifies daemon request relay, status reporting, and orderly shutdown.
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Tests;

public sealed class DaemonServerTests
{
    /// <summary>
    /// Verifies status, bridge relay, stop acknowledgment, and final pipe shutdown as one daemon lifecycle.
    /// </summary>
    [Fact]
    public async Task RelaysBridgeCallsAndStopsCleanly()
    {
        var pipeName = DaemonPipeNames.ForCurrentUser();
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var server = new DaemonServer(pipeName, TimeSpan.FromSeconds(3));
        var serverTask = server.RunAsync(cancellationSource.Token);
        var daemonClient = new DaemonClient(
            "unused.exe",
            pipeName,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3));

        try
        {
            var status = await WaitForStatus(daemonClient, TestContext.Current.CancellationToken);
            Assert.True(status.Running);
            Assert.Equal(Environment.ProcessId, status.ProcessId);

            var bridgeTask = FakePipeServer.ServeOnceAsync(request => FakePipeServer.Success(
                request,
                new CaptureResponse { IsProxyAttached = true }));
            var bridgeClient = new DaemonBridgeClient(daemonClient);
            var response = await bridgeClient.SendAsync<SetCaptureRequest, CaptureResponse>(
                Operations.SetCapture,
                new SetCaptureRequest { Enabled = true },
                TestContext.Current.CancellationToken);
            var request = await bridgeTask;

            Assert.True(response.IsProxyAttached);
            Assert.Equal(Operations.SetCapture, request.Operation);

            var stop = await daemonClient.StopAsync(TestContext.Current.CancellationToken);
            Assert.True(stop.WasRunning);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Null(await daemonClient.TryGetStatusAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            cancellationSource.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Polls until the test daemon accepts status requests or the bounded attempt count expires.
    /// </summary>
    /// <param name="client">The daemon client used for status probes.</param>
    /// <param name="cancellationToken">Cancels probes and polling delays.</param>
    private static async Task<DaemonStatus> WaitForStatus(
        DaemonClient client,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var status = await client.TryGetStatusAsync(cancellationToken);
            if (status is not null)
            {
                return status;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException("The test CLI daemon did not start.");
    }
}
