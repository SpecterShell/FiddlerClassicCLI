// Verifies bridge server framing and request dispatch over a real named pipe.
using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Web.Script.Serialization;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed class BridgeServerTests
{
    /// <summary>
    /// Verifies a secured listener accepts a framed request, invokes dispatch, and returns the correlated response.
    /// </summary>
    [Fact]
    public async Task AcceptedConnectionIsPassedToHandlerAndReceivesResponse()
    {
        TestEnvironment.EnsureInitialized();
        var logs = new ConcurrentQueue<string>();
        var pipeName = $"fiddler-classic-bridge-test-{Guid.NewGuid():N}";
        using var server = new BridgeServer(
            request => new BridgeResponse
            {
                ProtocolVersion = ProtocolConstants.Version,
                RequestId = request.RequestId,
                Success = false,
                Error = new BridgeError { Code = ErrorCodes.InvalidRequest, Message = "Test response." }
            },
            logs.Enqueue,
            pipeName);
        server.Start();

        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            client.Connect(3000);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"The test bridge did not accept a connection. {string.Join(Environment.NewLine, logs)}");
        }
        var serializer = new JavaScriptSerializer();
        Assert.Equal(pipeName, server.Status.PipeName);
        Assert.Equal(BridgeListenerState.Listening, server.Status.State);
        var request = new BridgeRequest
        {
            ProtocolVersion = ProtocolConstants.Version,
            RequestId = "regression-probe",
            Operation = "invalid.regression-probe",
            PayloadJson = "{}"
        };

        await FrameCodec.WriteAsync(client, serializer.Serialize(request), TestContext.Current.CancellationToken);
        var responseJson = await FrameCodec.ReadAsync(client, TestContext.Current.CancellationToken);
        var response = serializer.Deserialize<BridgeResponse>(responseJson);

        Assert.NotNull(response);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Equal(ErrorCodes.InvalidRequest, response.Error.Code);
    }

    [Fact]
    public async Task ReportsListenerFailureAndClearsItAfterRecovery()
    {
        var pipeName = "fiddler-listener-state-test-" + Guid.NewGuid().ToString("N");
        using var blocker = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var server = new BridgeServer(_ => new BridgeResponse(), _ => { }, pipeName);
        var original = server.Status;
        Assert.Equal(BridgeListenerState.Stopped, original.State);
        server.Start();
        await WaitForStateAsync(server, BridgeListenerState.Retrying);
        Assert.False(string.IsNullOrWhiteSpace(server.Status.Error));
        Assert.Equal(BridgeListenerState.Stopped, original.State);

        blocker.Dispose();
        await WaitForStateAsync(server, BridgeListenerState.Listening);
        Assert.Null(server.Status.Error);
        server.Dispose();
        Assert.Equal(BridgeListenerState.Stopped, server.Status.State);
        Assert.Null(server.Status.Error);
    }

    [Fact]
    public async Task DisposalDuringStartupCannotRestoreListeningState()
    {
        using var server = new BridgeServer(_ => new BridgeResponse(), _ => { },
            "fiddler-startup-state-test-" + Guid.NewGuid().ToString("N"));
        server.Start();
        server.Dispose();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(BridgeListenerState.Stopped, server.Status.State);
        using var client = new NamedPipeClientStream(".", server.Status.PipeName, PipeDirection.InOut);
        Assert.Throws<TimeoutException>(() => client.Connect(100));
    }

    private static async Task WaitForStateAsync(BridgeServer server, BridgeListenerState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.Status.State != state && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(state, server.Status.State);
    }
}
