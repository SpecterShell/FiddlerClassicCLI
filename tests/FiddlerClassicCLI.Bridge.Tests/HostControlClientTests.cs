// Verifies complete daemon exchange deadlines and cancellation against isolated named-pipe peers.
using System.IO.Pipes;
using System.Web.Script.Serialization;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class HostControlClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimesOutAConnectedPeerThatNeverCompletesItsResponse(bool partialFrame)
    {
        using var server = CreateServer(out var pipeName);
        // The exchange deadline includes cold-start serialization and pipe scheduling on CI.
        var client = new HostControlClient(pipeName, TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Framework pipe reads need disposal to interrupt pending I/O if the test deadline expires.
        using var abortOnCancellation = deadline.Token.Register(server.Dispose);
        var connection = server.WaitForConnectionAsync(deadline.Token);
        var exchange = client.GetServiceStatusAsync(deadline.Token);
        await connection;
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
            PayloadJson = serializer.Serialize(new DaemonStatus
            {
                Capabilities = new[] { DaemonProtocol.ManagedHttpCapability },
                HttpService = new HttpServiceStatus { Port = 9001 }
            })
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendsStartupAndCompleteConfigurationContracts(bool startup)
    {
        using var server = CreateServer(out var pipeName);
        var client = new HostControlClient(pipeName, TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var configuration = new ConfigureHttpServiceRequest
        {
            BindMode = HttpBindModes.Selected, BindAddresses = new[] { "127.0.0.1", "10.0.0.2" }, Port = 9012,
            StartupMode = HttpStartupModes.Enabled, AuthenticationMode = HttpAuthenticationModes.None, Confirm = true
        };
        var exchange = startup ? client.ApplyStartupAsync(deadline.Token) : client.ConfigureServiceAsync(configuration, deadline.Token);
        await RespondToCapabilityCheckAsync(server, DaemonProtocol.ManagedHttpCapability, deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        var serializer = new JavaScriptSerializer();
        var request = serializer.Deserialize<DaemonRequest>(await FrameCodec.ReadAsync(server, deadline.Token));
        Assert.Equal(startup ? DaemonProtocol.ApplyHttpStartup : DaemonProtocol.ConfigureHttpService, request.Method);
        if (!startup)
        {
            var payload = serializer.Deserialize<ConfigureHttpServiceRequest>(request.PayloadJson);
            Assert.Equal(configuration.BindMode, payload.BindMode);
            Assert.Equal(configuration.BindAddresses, payload.BindAddresses);
            Assert.Equal(configuration.Port, payload.Port);
            Assert.Equal(configuration.StartupMode, payload.StartupMode);
            Assert.Equal(HttpAuthenticationModes.None, payload.AuthenticationMode);
            Assert.True(payload.Confirm);
        }
        await FrameCodec.WriteAsync(server, serializer.Serialize(new DaemonResponse
        {
            RequestId = request.RequestId, Success = true,
            PayloadJson = serializer.Serialize(new HttpServiceStatus { AuthenticationMode = HttpAuthenticationModes.None, StartupMode = HttpStartupModes.Enabled })
        }), deadline.Token);
        var result = await exchange;
        Assert.Equal(HttpAuthenticationModes.None, result.AuthenticationMode);
        Assert.Equal(HttpStartupModes.Enabled, result.StartupMode);
    }

    private static NamedPipeServerStream CreateServer(out string pipeName)
    {
        pipeName = "fiddler-control-test-" + Guid.NewGuid().ToString("N");
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }
}
