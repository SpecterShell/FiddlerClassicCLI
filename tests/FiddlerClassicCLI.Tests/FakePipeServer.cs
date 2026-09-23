// Provides a one-request named-pipe bridge peer for host tests.
using System.IO.Pipes;
using System.Text.Json;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

internal static class FakePipeServer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Accepts one bridge connection, applies a response delegate, and returns the observed request.
    /// </summary>
    /// <param name="responder">Builds the correlated bridge response.</param>
    public static async Task<BridgeRequest> ServeOnceAsync(Func<BridgeRequest, BridgeResponse> responder)
    {
        await using var server = new NamedPipeServerStream(
            PipeNames.ForCurrentUser(),
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var requestJson = await FrameCodec.ReadAsync(server, TestContext.Current.CancellationToken);
        var request = JsonSerializer.Deserialize<BridgeRequest>(requestJson!, JsonOptions)!;
        await FrameCodec.WriteAsync(
            server,
            JsonSerializer.Serialize(responder(request), JsonOptions),
            TestContext.Current.CancellationToken);
        return request;
    }

    /// <summary>
    /// Creates a successful response correlated to a captured test request.
    /// </summary>
    /// <typeparam name="T">The response payload type.</typeparam>
    /// <param name="request">The captured bridge request.</param>
    /// <param name="payload">The typed successful payload.</param>
    public static BridgeResponse Success<T>(BridgeRequest request, T payload)
    {
        return new BridgeResponse
        {
            ProtocolVersion = ProtocolConstants.Version,
            RequestId = request.RequestId,
            Success = true,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions)
        };
    }
}
