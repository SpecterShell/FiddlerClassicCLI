// Provides a delegate-backed bridge client for isolated host and MCP tests.
using FiddlerClassic.Host.Bridge;

namespace FiddlerClassic.Tests;

internal sealed class TestBridgeClient : IBridgeClient
{
    public Func<string, object, object> Handler { get; set; } = (_, _) => throw new InvalidOperationException("No test response configured.");

    public Task<TResponse> SendAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((TResponse)Handler(operation, request!));
    }
}
