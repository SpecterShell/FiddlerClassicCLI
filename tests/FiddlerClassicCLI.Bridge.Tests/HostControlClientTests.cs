// Verifies complete daemon exchange deadlines and cancellation against isolated named-pipe peers.
using System.IO.Pipes;
using System.Web.Script.Serialization;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed class HostControlClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimesOutAConnectedPeerThatNeverCompletesItsResponse(bool partialFrame)
    {
        using var server = CreateServer(out var pipeName);
        var client = new HostControlClient(pipeName, TimeSpan.FromMilliseconds(500));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exchange = client.GetServiceStatusAsync(deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        Assert.NotNull(await FrameCodec.ReadAsync(server, deadline.Token));
        if (partialFrame)
        {
            // A valid header followed by a truncated body must obey the same exchange deadline.
            var prefix = BitConverter.GetBytes(100);
            await server.WriteAsync(prefix, 0, prefix.Length, deadline.Token);
            await server.FlushAsync(deadline.Token);
        }

        var error = await Assert.ThrowsAsync<TimeoutException>(() => exchange);
        Assert.Contains("operation may have completed", error.Message);
        Assert.False(deadline.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelsAConnectedPeerWithoutReportingATimeout()
    {
        using var server = CreateServer(out var pipeName);
        var client = new HostControlClient(pipeName, TimeSpan.FromSeconds(10));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var exchange = client.GetServiceStatusAsync(cancellation.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        await FrameCodec.ReadAsync(server, deadline.Token);
        cancellation.Cancel();

        Assert.Same(exchange, await Task.WhenAny(exchange, Task.Delay(3000, deadline.Token)));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ReturnsACorrelatedResponseBeforeTheDeadline()
    {
        using var server = CreateServer(out var pipeName);
        var client = new HostControlClient(pipeName, TimeSpan.FromSeconds(3));
        Assert.Equal(pipeName, client.PipeName);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exchange = client.GetServiceStatusAsync(deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        var serializer = new JavaScriptSerializer();
        var request = serializer.Deserialize<DaemonRequest>(await FrameCodec.ReadAsync(server, deadline.Token));
        await FrameCodec.WriteAsync(server, serializer.Serialize(new DaemonResponse
        {
            RequestId = request.RequestId,
            Success = true,
            PayloadJson = serializer.Serialize(new HttpServiceStatus { Port = 9001 })
        }), deadline.Token);
        Assert.Equal(9001, (await exchange).Port);
    }

    [Fact]
    public async Task BoundsConnectionEstablishment()
    {
        var client = new HostControlClient("absent-daemon-test-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(100));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetServiceStatusAsync(deadline.Token));
        Assert.False(deadline.IsCancellationRequested);
    }

    private static NamedPipeServerStream CreateServer(out string pipeName)
    {
        pipeName = "fiddler-control-test-" + Guid.NewGuid().ToString("N");
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }
}
