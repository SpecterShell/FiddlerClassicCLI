// Verifies bridge server framing and request dispatch over a real named pipe.
using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Web.Script.Serialization;
using FiddlerClassic.Bridge;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge.Tests;

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
}
