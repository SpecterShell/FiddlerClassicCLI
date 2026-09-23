// Runs the per-user daemon that serializes and relays CLI requests to the Fiddler bridge.
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Daemon;

internal sealed class DaemonServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly TimeSpan _bridgeTimeout;
    private readonly HttpServiceManager _httpService;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private int _nextConnectionId;

    public DaemonServer()
        : this(DaemonPipeNames.ForCurrentUser(), TimeSpan.FromSeconds(70), new ConfigStore())
    {
    }

    /// <summary>
    /// Creates a daemon server with an explicit pipe identity and bridge relay timeout.
    /// </summary>
    /// <param name="pipeName">The current-user pipe that accepts CLI clients.</param>
    /// <param name="bridgeTimeout">The maximum duration of a relayed Fiddler bridge exchange.</param>
    internal DaemonServer(string pipeName, TimeSpan bridgeTimeout)
        : this(pipeName, bridgeTimeout, new ConfigStore())
    {
    }

    internal DaemonServer(string pipeName, TimeSpan bridgeTimeout, ConfigStore configStore)
    {
        _pipeName = pipeName;
        _bridgeTimeout = bridgeTimeout;
        _httpService = new HttpServiceManager(configStore);
    }

    /// <summary>
    /// Acquires single-instance ownership, accepts clients, and drains active connections during shutdown.
    /// </summary>
    /// <param name="cancellationToken">Stops acceptance and active connection processing.</param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var ownership = CreateOwnershipPipe();
        try
        {
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _stopSource.Token);
            await _httpService.InitializeAsync(linkedSource.Token).ConfigureAwait(false);

            while (!linkedSource.IsCancellationRequested)
            {
                var listener = CreatePipe();
                try
                {
                    await listener.WaitForConnectionAsync(linkedSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
                {
                    listener.Dispose();
                    break;
                }

                var connectionId = Interlocked.Increment(ref _nextConnectionId);
                var connectionTask = HandleConnectionAsync(listener, linkedSource.Token);
                _connections[connectionId] = connectionTask;
                _ = connectionTask.ContinueWith(
                    completedTask => _connections.TryRemove(connectionId, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            await Task.WhenAll(_connections.Values.ToArray()).ConfigureAwait(false);
        }
        finally
        {
            await _httpService.DisposeAsync().ConfigureAwait(false);
            _stopSource.Dispose();
        }
    }

    /// <summary>
    /// Reads and answers one framed daemon request, then triggers shutdown after a successful stop request.
    /// </summary>
    /// <param name="connection">The connected current-user named pipe.</param>
    /// <param name="cancellationToken">Cancels reads, relay work, and response writes.</param>
    private async Task HandleConnectionAsync(NamedPipeServerStream connection, CancellationToken cancellationToken)
    {
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                var requestJson = await FrameCodec.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
                if (requestJson is null)
                {
                    return;
                }

                var request = JsonSerializer.Deserialize<DaemonRequest>(requestJson, JsonOptions)
                    ?? throw new InvalidDataException("The CLI daemon request is invalid.");
                var response = await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
                await FrameCodec.WriteAsync(
                    connection,
                    JsonSerializer.Serialize(response, JsonOptions),
                    cancellationToken).ConfigureAwait(false);

                if (response.Success && string.Equals(request.Method, DaemonProtocol.Stop, StringComparison.Ordinal))
                {
                    _stopSource.Cancel();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                // The client receives structured errors for valid envelopes. Malformed envelopes are dropped.
            }
        }
    }

    /// <summary>
    /// Validates and executes status, stop, or bridge-relay requests with stable error mapping.
    /// </summary>
    /// <param name="request">The deserialized daemon request envelope.</param>
    /// <param name="cancellationToken">Cancels bridge relay work.</param>
    private async Task<DaemonResponse> HandleRequestAsync(DaemonRequest request, CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != DaemonProtocol.Version)
        {
            return Error(
                request,
                ErrorCodes.ProtocolMismatch,
                $"CLI daemon protocol version {request.ProtocolVersion} is unsupported. Expected {DaemonProtocol.Version}.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return Error(request, ErrorCodes.InvalidRequest, "The CLI daemon request ID is required.");
        }

        try
        {
            switch (request.Method)
            {
                case DaemonProtocol.Status:
                    return Success(request, JsonSerializer.Serialize(CreateStatus(), JsonOptions));

                case DaemonProtocol.Stop:
                    return Success(request, JsonSerializer.Serialize(new DaemonStopResult { WasRunning = true }, JsonOptions));

                case DaemonProtocol.Relay:
                    ValidateBridgeRequest(request.PayloadJson);
                    using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeoutSource.CancelAfter(_bridgeTimeout);
                        var responseJson = await NamedPipeFrameClient.ExchangeAsync(
                            PipeNames.ForCurrentUser(),
                            request.PayloadJson,
                            timeoutSource.Token).ConfigureAwait(false);
                        return Success(request, responseJson);
                    }

                case DaemonProtocol.HttpServiceStatus:
                    return Success(request, JsonSerializer.Serialize(_httpService.GetStatus(), JsonOptions));

                case DaemonProtocol.ConfigureHttpService:
                    return Success(
                        request,
                        JsonSerializer.Serialize(
                            await _httpService.ConfigureAsync(
                                ReadPayload<ConfigureHttpServiceRequest>(request),
                                cancellationToken).ConfigureAwait(false),
                            JsonOptions));

                case DaemonProtocol.EnableHttpService:
                    return Success(
                        request,
                        JsonSerializer.Serialize(
                            await _httpService.EnableAsync(
                                ReadPayload<SetHttpServiceEnabledRequest>(request).Confirm,
                                cancellationToken).ConfigureAwait(false),
                            JsonOptions));

                case DaemonProtocol.DisableHttpService:
                    return Success(
                        request,
                        JsonSerializer.Serialize(
                            await _httpService.DisableAsync(
                                ReadPayload<SetHttpServiceEnabledRequest>(request).Confirm,
                                cancellationToken).ConfigureAwait(false),
                            JsonOptions));

                case DaemonProtocol.ListHttpClients:
                    return Success(request, JsonSerializer.Serialize(_httpService.ListClients(), JsonOptions));

                case DaemonProtocol.AuthorizeHttpClient:
                    return Success(
                        request,
                        JsonSerializer.Serialize(
                            _httpService.AuthorizeClient(ReadPayload<AuthorizeHttpClientRequest>(request)),
                            JsonOptions));

                case DaemonProtocol.DeauthorizeHttpClient:
                    return Success(
                        request,
                        JsonSerializer.Serialize(
                            _httpService.DeauthorizeClient(ReadPayload<DeauthorizeHttpClientRequest>(request)),
                            JsonOptions));

                case DaemonProtocol.ListHttpConnections:
                    return Success(request, JsonSerializer.Serialize(_httpService.ListConnections(), JsonOptions));

                case DaemonProtocol.DisconnectHttpConnection:
                    return Success(
                        request,
                        JsonSerializer.Serialize(
                            _httpService.Disconnect(ReadPayload<DisconnectHttpConnectionRequest>(request)),
                            JsonOptions));

                default:
                    return Error(request, ErrorCodes.InvalidRequest, $"Unknown CLI daemon method '{request.Method}'.");
            }
        }
        catch (HttpAdministrationException exception)
        {
            return Error(request, exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(
                request,
                ErrorCodes.Timeout,
                "Timed out waiting for Fiddler Classic. Verify that Fiddler is running and the bridge is installed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Error(
                request,
                ErrorCodes.Unavailable,
                "Fiddler Classic is unavailable. Start Fiddler and verify the bridge with 'fiddler-classic-cli doctor'.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return Error(request, ErrorCodes.InvalidRequest, exception.Message);
        }
        catch (Exception exception)
        {
            return Error(request, ErrorCodes.Internal, exception.Message);
        }
    }

    private static T ReadPayload<T>(DaemonRequest request)
    {
        return JsonSerializer.Deserialize<T>(request.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException($"The payload for '{request.Method}' is invalid.");
    }

    /// <summary>
    /// Verifies a relayed payload is a complete current-protocol bridge request.
    /// </summary>
    /// <param name="requestJson">The serialized bridge request envelope.</param>
    private static void ValidateBridgeRequest(string requestJson)
    {
        var request = JsonSerializer.Deserialize<BridgeRequest>(requestJson, JsonOptions)
            ?? throw new InvalidDataException("The relayed bridge request is invalid.");
        if (request.ProtocolVersion != ProtocolConstants.Version)
        {
            throw new InvalidDataException(
                $"Bridge protocol version {request.ProtocolVersion} is unsupported. Expected {ProtocolConstants.Version}.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.Operation))
        {
            throw new InvalidDataException("The relayed bridge request is missing its request ID or operation.");
        }
    }

    /// <summary>
    /// Captures the daemon's process identity, start time, pipe, and informational host version.
    /// </summary>
    private DaemonStatus CreateStatus()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return new DaemonStatus
        {
            Running = true,
            ProcessId = Environment.ProcessId,
            StartedAtUtc = _startedAtUtc.ToString("O"),
            PipeName = _pipeName,
            HostVersion = informationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown",
            Capabilities = new[] { DaemonProtocol.ManagedHttpCapability },
            HttpService = _httpService.GetStatus()
        };
    }

    /// <summary>
    /// Creates an asynchronous client pipe restricted to the current Windows user.
    /// </summary>
    private NamedPipeServerStream CreatePipe()
    {
        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            64 * 1024,
            64 * 1024);
    }

    /// <summary>
    /// Acquires a first-instance pipe that prevents multiple daemons for the same user.
    /// </summary>
    private NamedPipeServerStream CreateOwnershipPipe()
    {
        return DaemonOwnership.TryAcquire(_pipeName)
            ?? throw new InvalidOperationException("Daemon ownership is unavailable. Another daemon or offline configuration operation may be active. Retry startup.");
    }

    private static DaemonResponse Success(DaemonRequest request, string payloadJson)
    {
        return new DaemonResponse
        {
            RequestId = request.RequestId,
            Success = true,
            PayloadJson = payloadJson
        };
    }

    private static DaemonResponse Error(DaemonRequest request, string code, string message)
    {
        return new DaemonResponse
        {
            RequestId = request.RequestId,
            Success = false,
            Error = new BridgeError { Code = code, Message = message }
        };
    }
}
