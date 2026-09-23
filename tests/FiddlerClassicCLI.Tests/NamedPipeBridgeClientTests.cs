// Verifies typed bridge exchanges and stable errors over a fake named pipe.
using System.Text.Json;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class NamedPipeBridgeClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Verifies a typed request and response retain protocol version, operation, and payload values.
    /// </summary>
    [Fact]
    public async Task ExchangesVersionedFramedMessages()
    {
        var serverTask = FakePipeServer.ServeOnceAsync(request => new BridgeResponse
        {
            ProtocolVersion = ProtocolConstants.Version,
            RequestId = request.RequestId,
            Success = true,
            PayloadJson = JsonSerializer.Serialize(new CaptureResponse { IsProxyAttached = true }, JsonOptions)
        });
        var client = new NamedPipeBridgeClient(TimeSpan.FromSeconds(3));

        var response = await client.SendAsync<SetCaptureRequest, CaptureResponse>(
            Operations.SetCapture,
            new SetCaptureRequest { Enabled = true },
            TestContext.Current.CancellationToken);

        Assert.True(response.IsProxyAttached);
        var request = await serverTask;
        Assert.Equal(ProtocolConstants.Version, request.ProtocolVersion);
        Assert.Equal(Operations.SetCapture, request.Operation);
        Assert.True(JsonSerializer.Deserialize<SetCaptureRequest>(request.PayloadJson, JsonOptions)!.Enabled);
    }

    /// <summary>
    /// Verifies bridge error codes and messages survive the named-pipe client boundary.
    /// </summary>
    [Fact]
    public async Task PreservesStableBridgeErrors()
    {
        var serverTask = FakePipeServer.ServeOnceAsync(request => new BridgeResponse
        {
            ProtocolVersion = ProtocolConstants.Version,
            RequestId = request.RequestId,
            Success = false,
            Error = new BridgeError { Code = ErrorCodes.NotFound, Message = "Session 42 was not found." }
        });
        var client = new NamedPipeBridgeClient(TimeSpan.FromSeconds(3));

        var exception = await Assert.ThrowsAsync<BridgeClientException>(() =>
            client.SendAsync<GetSessionDetailsRequest, SessionDetails>(
                Operations.GetSessionDetails,
                new GetSessionDetailsRequest { SessionId = 42 },
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.NotFound, exception.Code);
        Assert.Contains("42", exception.Message);
        await serverTask;
    }
}
