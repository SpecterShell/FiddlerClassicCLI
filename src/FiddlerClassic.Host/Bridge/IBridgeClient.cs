// Defines the shared asynchronous contract for communicating with the Fiddler bridge.
namespace FiddlerClassic.Host.Bridge;

internal interface IBridgeClient
{
    /// <summary>
    /// Sends one typed operation and returns its typed successful response payload.
    /// </summary>
    /// <typeparam name="TRequest">The operation payload type.</typeparam>
    /// <typeparam name="TResponse">The expected successful response type.</typeparam>
    /// <param name="operation">The bridge operation name.</param>
    /// <param name="request">The typed operation payload.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    Task<TResponse> SendAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        CancellationToken cancellationToken = default);
}
