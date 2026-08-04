// Serializes bridge requests and validates typed bridge responses.
using System.Text.Json;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Bridge;

internal static class BridgeMessageCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Wraps a typed operation payload in a versioned request envelope with a unique correlation ID.
    /// </summary>
    /// <typeparam name="TRequest">The operation payload type.</typeparam>
    /// <param name="operation">The protocol operation name.</param>
    /// <param name="request">The typed operation payload.</param>
    public static EncodedBridgeRequest CreateRequest<TRequest>(string operation, TRequest request)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var envelope = new BridgeRequest
        {
            ProtocolVersion = ProtocolConstants.Version,
            RequestId = requestId,
            Operation = operation,
            PayloadJson = JsonSerializer.Serialize(request, JsonOptions)
        };

        return new EncodedBridgeRequest(requestId, JsonSerializer.Serialize(envelope, JsonOptions));
    }

    /// <summary>
    /// Validates a response envelope and deserializes its successful payload.
    /// </summary>
    /// <typeparam name="TResponse">The expected successful payload type.</typeparam>
    /// <param name="responseJson">The complete response envelope returned by the bridge.</param>
    /// <param name="requestId">The request correlation ID that the response must match.</param>
    public static TResponse ParseResponse<TResponse>(string responseJson, string requestId)
    {
        var response = JsonSerializer.Deserialize<BridgeResponse>(responseJson, JsonOptions)
            ?? throw new BridgeClientException(ErrorCodes.InvalidRequest, "The Fiddler bridge returned an invalid response.");

        if (response.ProtocolVersion != ProtocolConstants.Version)
        {
            throw new BridgeClientException(
                ErrorCodes.ProtocolMismatch,
                $"Bridge protocol version {response.ProtocolVersion} is unsupported; expected {ProtocolConstants.Version}.");
        }

        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new BridgeClientException(ErrorCodes.InvalidRequest, "The Fiddler bridge response did not match the request.");
        }

        if (!response.Success)
        {
            throw new BridgeClientException(
                response.Error?.Code ?? ErrorCodes.Internal,
                response.Error?.Message ?? "The Fiddler bridge operation failed.");
        }

        return JsonSerializer.Deserialize<TResponse>(response.PayloadJson, JsonOptions)
            ?? throw new BridgeClientException(ErrorCodes.InvalidRequest, "The Fiddler bridge returned an empty payload.");
    }
}

internal sealed record EncodedBridgeRequest(string RequestId, string Json);
