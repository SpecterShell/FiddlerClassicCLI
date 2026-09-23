// Owns the daemon-managed MCP HTTP listener, credentials, and active connections.
using System.Net;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Mcp;

internal sealed class HttpServiceManager : IAsyncDisposable
{
    private readonly ConfigStore _configStore;
    private readonly HttpCredentialManager _credentials;
    private readonly Func<HttpInterfaceAddressDto[]> _readInterfaces;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HttpConnectionRegistry _connections = new();
    private ActiveListener? _listener;
    private string? _lastError;

    // Keep the applied transport settings with its owner. File edits must never make an
    // anonymous running listener appear authenticated before it has actually restarted.
    private sealed record ActiveListener(ManagedHttpServer Server, string BindMode,
        string[] BindAddresses, int Port, string AuthenticationMode);

    public HttpServiceManager(ConfigStore configStore, Func<HttpInterfaceAddressDto[]>? readInterfaces = null)
    {
        _configStore = configStore;
        _credentials = new HttpCredentialManager(configStore);
        _readInterfaces = readInterfaces ?? (() => HttpEndpointResolver.ReadInterfaceAddresses());
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ApplyStartupAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies startup policy once per daemon startup or explicit extension initialization.</summary>
    /// <param name="cancellationToken">Cancels listener changes and preserves caller cancellation.</param>
    /// <returns>Listener state, including a bind error if the configured endpoints are unavailable.</returns>
    public async Task<HttpServiceStatus> ApplyStartupAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = _configStore.ApplyHttpStartup();
            try
            {
                if (configuration.HttpServiceEnabled)
                    await StartUnsafeAsync(configuration, cancellationToken).ConfigureAwait(false);
                else
                    await StopUnsafeAsync().ConfigureAwait(false);
                _lastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _lastError = exception.Message;
            }
            return CreateStatus(configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    public HttpServiceStatus GetStatus()
    {
        var configuration = _configStore.GetOrCreate();
        return CreateStatus(configuration);
    }

    public async Task<HttpServiceStatus> ConfigureAsync(
        ConfigureHttpServiceRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_listener is not null && (request.BindMode is not null || request.BindAddresses is not null
                || request.Port.HasValue || request.AuthenticationMode is not null))
                throw new HttpAdministrationException(ErrorCodes.Conflict,
                    "Disable the managed MCP HTTP service before changing its bindings, port, or authentication.");
            var configuration = _configStore.ConfigureHttpService(request);
            // A startup-only edit does not retry a failed listener or resolve its bind error.
            if (!configuration.HttpServiceEnabled) _lastError = null;
            return CreateStatus(configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HttpServiceStatus> EnableAsync(bool confirmRemote, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = _configStore.SetHttpServiceEnabled(true, confirmRemote);
            if (_listener is null)
            {
                try
                {
                    await StartUnsafeAsync(configuration, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _lastError = exception.Message;
                    throw new HttpAdministrationException(
                        ErrorCodes.Unavailable,
                        $"The managed MCP HTTP service could not start: {exception.Message}",
                        exception);
                }
            }

            return CreateStatus(configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HttpServiceStatus> DisableAsync(bool confirmConnections, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connections.Count > 0 && !confirmConnections)
            {
                throw new HttpAdministrationException(
                    ErrorCodes.ConfirmationRequired,
                    "Disabling the managed MCP HTTP service will disconnect active clients and requires confirmation.");
            }

            var configuration = _configStore.SetHttpServiceEnabled(false);
            await StopUnsafeAsync().ConfigureAwait(false);
            _lastError = null;
            return CreateStatus(configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ListHttpClientsResponse ListClients()
    {
        var configuration = _configStore.GetOrCreate();
        var clients = new List<AuthorizedHttpClientDto>
        {
            CreateClient(
                HttpClientIds.Default,
                "Default CLI token",
                isDefault: true,
                configuration.HttpDefaultTokenCreatedAtUtc)
        };
        clients.AddRange(configuration.AuthorizedHttpClients.Select(client =>
            CreateClient(client.ClientId, client.Name, isDefault: false, client.CreatedAtUtc)));
        return new ListHttpClientsResponse { Clients = clients };
    }

    public AuthorizeHttpClientResponse AuthorizeClient(AuthorizeHttpClientRequest request)
    {
        var secret = _configStore.AuthorizeClient(request.Name);
        _credentials.Reload();
        return new AuthorizeHttpClientResponse
        {
            Client = CreateClient(
                secret.Client.ClientId,
                secret.Client.Name,
                isDefault: false,
                secret.Client.CreatedAtUtc),
            Token = secret.Token
        };
    }

    public DeauthorizeHttpClientResponse DeauthorizeClient(DeauthorizeHttpClientRequest request)
    {
        if (!request.Confirm)
        {
            throw new HttpAdministrationException(
                ErrorCodes.ConfirmationRequired,
                "Deauthorizing an HTTP client requires explicit confirmation.");
        }

        var deauthorized = _configStore.DeauthorizeClient(request.ClientId);
        _credentials.Reload();
        var disconnected = _connections.DisconnectClient(deauthorized.ClientId);
        return new DeauthorizeHttpClientResponse
        {
            ClientId = deauthorized.ClientId,
            DefaultTokenRotated = deauthorized.DefaultTokenRotated,
            DisconnectedConnectionCount = disconnected
        };
    }

    public ListHttpConnectionsResponse ListConnections()
    {
        return _connections.List();
    }

    public DisconnectHttpConnectionResponse Disconnect(DisconnectHttpConnectionRequest request)
    {
        if (!request.Confirm)
        {
            throw new HttpAdministrationException(
                ErrorCodes.ConfirmationRequired,
                "Disconnecting an HTTP connection requires explicit confirmation.");
        }

        var disconnected = _connections.Disconnect(request.ConnectionId);
        if (!disconnected)
        {
            throw new HttpAdministrationException(
                ErrorCodes.NotFound,
                $"HTTP connection '{request.ConnectionId}' was not found.");
        }

        return new DisconnectHttpConnectionResponse
        {
            ConnectionId = request.ConnectionId,
            Disconnected = true
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task StartUnsafeAsync(HostConfiguration configuration, CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            return;
        }

        HttpListenerSettings.Validate(configuration);
        ConfigStore.ValidatePort(configuration.HttpPort);
        _connections = new HttpConnectionRegistry();
        var addresses = HttpListenerSettings.GetAddresses(configuration);
        if (configuration.HttpBindMode == HttpBindModes.Selected)
        {
            var available = new HashSet<string>(_readInterfaces().Select(item => item.Address), StringComparer.Ordinal);
            var missing = configuration.HttpBindAddresses.Where(address => !available.Contains(address)).ToArray();
            if (missing.Length != 0)
                throw new HttpAdministrationException(ErrorCodes.Unavailable,
                    $"Selected IPv4 addresses are unavailable: {string.Join(", ", missing)}. Disable MCP HTTP and choose active local addresses.");
        }
        var server = await McpHost.StartHttpAsync(
            addresses,
            configuration.HttpPort,
            _credentials,
            _connections,
            cancellationToken,
            authenticationMode: configuration.HttpAuthenticationMode).ConfigureAwait(false);
        Volatile.Write(ref _listener, new ActiveListener(server, configuration.HttpBindMode,
            configuration.HttpBindAddresses.ToArray(), configuration.HttpPort, configuration.HttpAuthenticationMode));
        _lastError = null;
    }

    private async Task StopUnsafeAsync()
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        await listener.Server.DisposeAsync().ConfigureAwait(false);
        Volatile.Write(ref _listener, null);
        _connections = new HttpConnectionRegistry();
    }

    private HttpServiceStatus CreateStatus(HostConfiguration configuration)
    {
        var listener = Volatile.Read(ref _listener);
        if (listener is not null)
        {
            configuration = new HostConfiguration
            {
                HttpServiceEnabled = configuration.HttpServiceEnabled,
                HttpStartupMode = configuration.HttpStartupMode,
                HttpBindMode = listener.BindMode,
                HttpBindAddresses = listener.BindAddresses,
                HttpPort = listener.Port,
                HttpAuthenticationMode = listener.AuthenticationMode
            };
        }
        return HttpEndpointResolver.FromConfiguration(configuration, listener is not null, _connections.Count, _lastError);
    }

    private AuthorizedHttpClientDto CreateClient(
        string clientId,
        string name,
        bool isDefault,
        string createdAtUtc)
    {
        return new AuthorizedHttpClientDto
        {
            ClientId = clientId,
            Name = name,
            IsDefault = isDefault,
            CreatedAtUtc = createdAtUtc,
            LastSeenAtUtc = _credentials.GetLastSeen(clientId)?.ToString("O"),
            ActiveConnectionCount = _connections.CountForClient(clientId)
        };
    }
}
