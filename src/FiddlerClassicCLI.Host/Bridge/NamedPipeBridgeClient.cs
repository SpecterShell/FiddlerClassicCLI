// Sends typed host operations to Fiddler through the secured bridge named pipe.
using System.Text.Json;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Bridge;

internal sealed class NamedPipeBridgeClient : IBridgeClient
{
    private readonly TimeSpan _timeout;

    public NamedPipeBridgeClient()
        : this(TimeSpan.FromSeconds(70))
    {
    }

    internal NamedPipeBridgeClient(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>
    /// Sends one typed request to the bridge and maps transport failures to stable client errors.
    /// </summary>
    /// <typeparam name="TRequest">The operation payload type.</typeparam>
    /// <typeparam name="TResponse">The expected successful response type.</typeparam>
    /// <param name="operation">The bridge operation name.</param>
    /// <param name="request">The typed operation payload.</param>
    /// <param name="cancellationToken">Cancels the exchange without changing the configured timeout.</param>
    public async Task<TResponse> SendAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        var encodedRequest = BridgeMessageCodec.CreateRequest(operation, request);

        try
        {
            var responseJson = await NamedPipeFrameClient.ExchangeAsync(
                PipeNames.ForCurrentUser(),
                encodedRequest.Json,
                timeoutSource.Token).ConfigureAwait(false);
            return BridgeMessageCodec.ParseResponse<TResponse>(responseJson, encodedRequest.RequestId);
        }
        catch (BridgeClientException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BridgeClientException(
                ErrorCodes.Timeout,
                "Timed out waiting for Fiddler Classic. Verify that Fiddler is running and the bridge is installed.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new BridgeClientException(
                ErrorCodes.Timeout,
                "Timed out waiting for Fiddler Classic. Verify that Fiddler is running and the bridge is installed.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new BridgeClientException(ErrorCodes.InvalidRequest, "The Fiddler bridge returned malformed JSON.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new BridgeClientException(ErrorCodes.InvalidRequest, "The Fiddler bridge returned an invalid frame.", exception);
        }
        catch (IOException exception)
        {
            throw Unavailable(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Unavailable(exception);
        }
    }

    private static BridgeClientException Unavailable(Exception exception)
    {
        return new BridgeClientException(
            ErrorCodes.Unavailable,
            "Fiddler Classic is unavailable. Start Fiddler and verify the bridge with 'fiddler-classic-cli doctor'.",
            exception);
    }
}
