// Starts, discovers, controls, and relays requests through the per-user CLI daemon.
using System.Diagnostics;
using System.Text.Json;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Daemon;

internal sealed class DaemonClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _executablePath;
    private readonly string _pipeName;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _requestTimeout;

    public DaemonClient()
        : this(
            Environment.ProcessPath ?? throw new InvalidOperationException("The current executable path is unavailable."),
            DaemonPipeNames.ForCurrentUser(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(75))
    {
    }

    /// <summary>
    /// Creates a daemon client with explicit process, pipe, startup, and request settings.
    /// </summary>
    /// <param name="executablePath">The host executable used to spawn the hidden daemon.</param>
    /// <param name="pipeName">The current-user daemon pipe name.</param>
    /// <param name="startTimeout">The maximum time to wait for startup or shutdown.</param>
    /// <param name="requestTimeout">The maximum time for normal daemon requests.</param>
    internal DaemonClient(
        string executablePath,
        string pipeName,
        TimeSpan startTimeout,
        TimeSpan requestTimeout)
    {
        _executablePath = executablePath;
        _pipeName = pipeName;
        _startTimeout = startTimeout;
        _requestTimeout = requestTimeout;
    }

    /// <summary>
    /// Reuses a healthy daemon or starts a hidden process and waits until its status endpoint responds.
    /// </summary>
    /// <param name="cancellationToken">Cancels discovery, process startup polling, and delays.</param>
    public async Task<DaemonStatus> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        var existing = await TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("daemon");
        startInfo.ArgumentList.Add("run");

        using var process = Process.Start(startInfo)
            ?? throw new DaemonClientException(ErrorCodes.Unavailable, "Could not start the Fiddler Classic CLI daemon.");
        var deadline = DateTime.UtcNow + _startTimeout;
        int? childExitCode = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DaemonStatus? status = null;
            try
            {
                status = await TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DaemonClientException exception) when (exception.Code == ErrorCodes.Timeout)
            {
                // The child can hold ownership while initializing its listener. Keep waiting within
                // the startup deadline. This never permits offline work or a second process launch.
            }
            if (status is not null)
            {
                return status;
            }

            if (process.HasExited)
            {
                childExitCode = process.ExitCode;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        var detail = childExitCode.HasValue
            ? $" The spawned process exited with code {childExitCode.Value}."
            : string.Empty;
        throw new DaemonClientException(
            childExitCode.HasValue ? ErrorCodes.Unavailable : ErrorCodes.Timeout,
            $"Timed out starting the Fiddler Classic CLI daemon.{detail} Run 'fiddler-classic-cli daemon status' for diagnostics.");
    }

    /// <summary>
    /// Returns null only when no control connection was established and daemon ownership is available.
    /// A connected, busy, inaccessible, or unresponsive peer remains an error and prevents offline fallback.
    /// </summary>
    /// <param name="cancellationToken">Cancels the status exchange.</param>
    public async Task<DaemonStatus?> TryGetStatusAsync(CancellationToken cancellationToken = default)
    {
        var connected = false;
        try
        {
            return await SendAsync<DaemonStatus>(
                DaemonProtocol.Status,
                "{}",
                TimeSpan.FromMilliseconds(500),
                cancellationToken,
                onConnected: () => connected = true).ConfigureAwait(false);
        }
        catch (DaemonClientException exception) when (exception.Code == ErrorCodes.Timeout && !connected)
        {
            using var ownership = DaemonOwnership.TryAcquire(_pipeName);
            if (ownership is null)
            {
                throw;
            }
            return null;
        }
    }

    /// <summary>Prevents daemon startup for the complete offline read or configuration transaction.</summary>
    internal IDisposable AcquireOfflineAccess()
    {
        return DaemonOwnership.TryAcquire(_pipeName)
            ?? throw new DaemonClientException(ErrorCodes.Unavailable,
                "Daemon ownership is unavailable. It may have started during the status check. Retry the operation.");
    }

    /// <summary>
    /// Requests an orderly shutdown and waits until the daemon releases its ownership pipe.
    /// </summary>
    /// <param name="cancellationToken">Cancels status checks, the stop request, and polling delays.</param>
    public async Task<DaemonStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (await TryGetStatusAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return new DaemonStopResult { WasRunning = false };
        }

        await SendAsync<DaemonStopResult>(
            DaemonProtocol.Stop,
            "{}",
            _requestTimeout,
            cancellationToken).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + _startTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ownership = DaemonOwnership.TryAcquire(_pipeName);
            if (ownership is not null)
            {
                return new DaemonStopResult { WasRunning = true };
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new DaemonClientException(ErrorCodes.Timeout, "Timed out stopping the Fiddler Classic CLI daemon.");
    }

    /// <summary>
    /// Ensures the daemon is running and relays one complete bridge request envelope through it.
    /// </summary>
    /// <param name="bridgeRequestJson">The serialized versioned bridge request.</param>
    /// <param name="cancellationToken">Cancels daemon startup or the relay exchange.</param>
    public async Task<string> RelayAsync(string bridgeRequestJson, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await SendAsync<string>(
            DaemonProtocol.Relay,
            bridgeRequestJson,
            _requestTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<HttpServiceStatus> GetHttpServiceStatusAsync(CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<EmptyRequest, HttpServiceStatus>(
            DaemonProtocol.HttpServiceStatus,
            new EmptyRequest(),
            cancellationToken);
    }

    public Task<HttpServiceStatus> ConfigureHttpServiceAsync(
        ConfigureHttpServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<ConfigureHttpServiceRequest, HttpServiceStatus>(
            DaemonProtocol.ConfigureHttpService,
            request,
            cancellationToken);
    }

    public Task<HttpServiceStatus> ApplyHttpStartupAsync(CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<EmptyRequest, HttpServiceStatus>(
            DaemonProtocol.ApplyHttpStartup, new EmptyRequest(), cancellationToken);
    }

    public Task<HttpServiceStatus> EnableHttpServiceAsync(
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<SetHttpServiceEnabledRequest, HttpServiceStatus>(
            DaemonProtocol.EnableHttpService,
            new SetHttpServiceEnabledRequest { Confirm = confirm },
            cancellationToken);
    }

    public Task<HttpServiceStatus> DisableHttpServiceAsync(
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<SetHttpServiceEnabledRequest, HttpServiceStatus>(
            DaemonProtocol.DisableHttpService,
            new SetHttpServiceEnabledRequest { Confirm = confirm },
            cancellationToken);
    }

    public Task<ListHttpClientsResponse> ListHttpClientsAsync(CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<EmptyRequest, ListHttpClientsResponse>(
            DaemonProtocol.ListHttpClients,
            new EmptyRequest(),
            cancellationToken);
    }

    public Task<AuthorizeHttpClientResponse> AuthorizeHttpClientAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<AuthorizeHttpClientRequest, AuthorizeHttpClientResponse>(
            DaemonProtocol.AuthorizeHttpClient,
            new AuthorizeHttpClientRequest { Name = name },
            cancellationToken);
    }

    public Task<DeauthorizeHttpClientResponse> DeauthorizeHttpClientAsync(
        string clientId,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<DeauthorizeHttpClientRequest, DeauthorizeHttpClientResponse>(
            DaemonProtocol.DeauthorizeHttpClient,
            new DeauthorizeHttpClientRequest { ClientId = clientId, Confirm = confirm },
            cancellationToken);
    }

    public Task<ListHttpConnectionsResponse> ListHttpConnectionsAsync(CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<EmptyRequest, ListHttpConnectionsResponse>(
            DaemonProtocol.ListHttpConnections,
            new EmptyRequest(),
            cancellationToken);
    }

    public Task<DisconnectHttpConnectionResponse> DisconnectHttpConnectionAsync(
        string connectionId,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        return SendManagedHttpAsync<DisconnectHttpConnectionRequest, DisconnectHttpConnectionResponse>(
            DaemonProtocol.DisconnectHttpConnection,
            new DisconnectHttpConnectionRequest { ConnectionId = connectionId, Confirm = confirm },
            cancellationToken);
    }

    private async Task<TResponse> SendManagedHttpAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        CancellationToken cancellationToken)
    {
        var status = await TryGetStatusAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new DaemonClientException(ErrorCodes.Unavailable, "The Fiddler Classic CLI daemon is not running.");
        if (!status.Capabilities.Contains(DaemonProtocol.ManagedHttpCapability, StringComparer.Ordinal))
        {
            throw new DaemonClientException(
                ErrorCodes.ProtocolMismatch,
                "The running CLI daemon does not support the current MCP HTTP settings. Install the matching CLI and bridge, then stop and restart the daemon.");
        }

        return await SendAsync<TResponse>(
            method,
            JsonSerializer.Serialize(request, JsonOptions),
            _requestTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exchanges one correlated daemon request and validates its protocol, identity, and result envelope.
    /// </summary>
    /// <typeparam name="T">The expected response payload type.</typeparam>
    /// <param name="method">The daemon protocol method.</param>
    /// <param name="payloadJson">The serialized method payload.</param>
    /// <param name="timeout">The operation-specific timeout.</param>
    /// <param name="cancellationToken">Cancels the named-pipe exchange.</param>
    /// <param name="onConnected">Records successful connection for stopped-daemon detection.</param>
    private async Task<T> SendAsync<T>(
        string method,
        string payloadJson,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action? onConnected = null)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var request = new DaemonRequest
        {
            RequestId = requestId,
            Method = method,
            PayloadJson = payloadJson
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var responseJson = await NamedPipeFrameClient.ExchangeAsync(
                _pipeName,
                JsonSerializer.Serialize(request, JsonOptions),
                timeoutSource.Token,
                onConnected).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<DaemonResponse>(responseJson, JsonOptions)
                ?? throw new DaemonClientException(ErrorCodes.InvalidRequest, "The CLI daemon returned an invalid response.");

            if (response.ProtocolVersion != DaemonProtocol.Version)
            {
                throw new DaemonClientException(
                    ErrorCodes.ProtocolMismatch,
                    $"CLI daemon protocol version {response.ProtocolVersion} is unsupported. Expected {DaemonProtocol.Version}.");
            }

            if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            {
                throw new DaemonClientException(ErrorCodes.InvalidRequest, "The CLI daemon response did not match the request.");
            }

            if (!response.Success)
            {
                throw new DaemonClientException(
                    response.Error?.Code ?? ErrorCodes.Internal,
                    response.Error?.Message ?? "The CLI daemon operation failed.");
            }

            if (typeof(T) == typeof(string))
            {
                return (T)(object)response.PayloadJson;
            }

            return JsonSerializer.Deserialize<T>(response.PayloadJson, JsonOptions)
                ?? throw new DaemonClientException(ErrorCodes.InvalidRequest, "The CLI daemon returned an empty payload.");
        }
        catch (DaemonClientException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DaemonClientException(ErrorCodes.Timeout, "Timed out waiting for the Fiddler Classic CLI daemon.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new DaemonClientException(
                ErrorCodes.Unavailable,
                "The Fiddler Classic CLI daemon is unavailable. Run 'fiddler-classic-cli daemon stop' and retry.",
                exception);
        }
    }
}

internal sealed class DaemonClientException : Exception
{
    public DaemonClientException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
