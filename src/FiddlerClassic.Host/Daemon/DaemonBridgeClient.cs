// Adapts the CLI daemon relay to the shared bridge-client abstraction.
using FiddlerClassic.Host.Bridge;

namespace FiddlerClassic.Host.Daemon;

internal sealed class DaemonBridgeClient : IBridgeClient
{
    private readonly DaemonClient _daemonClient;

    public DaemonBridgeClient(DaemonClient daemonClient)
    {
        _daemonClient = daemonClient;
    }

    /// <summary>
    /// Encodes a bridge operation, relays it through the daemon, and restores bridge-client error semantics.
    /// </summary>
    /// <typeparam name="TRequest">The operation payload type.</typeparam>
    /// <typeparam name="TResponse">The expected successful response type.</typeparam>
    /// <param name="operation">The bridge operation name.</param>
    /// <param name="request">The typed operation payload.</param>
    /// <param name="cancellationToken">Cancels daemon startup or relay work.</param>
    public async Task<TResponse> SendAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var encodedRequest = BridgeMessageCodec.CreateRequest(operation, request);
        try
        {
            var responseJson = await _daemonClient.RelayAsync(encodedRequest.Json, cancellationToken).ConfigureAwait(false);
            return BridgeMessageCodec.ParseResponse<TResponse>(responseJson, encodedRequest.RequestId);
        }
        catch (DaemonClientException exception)
        {
            throw new BridgeClientException(exception.Code, exception.Message, exception);
        }
    }
}
