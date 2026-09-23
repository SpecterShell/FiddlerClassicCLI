// Owns the daemon-managed MCP HTTP listener, credentials, and active connections.
using System.Net;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Mcp;

internal sealed class HttpServiceManager : IAsyncDisposable
{
    private readonly ConfigStore _configStore;
    private readonly HttpCredentialManager _credentials;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HttpConnectionRegistry _connections = new();
    private ManagedHttpServer? _server;
    private string? _lastError;

    public HttpServiceManager(ConfigStore configStore)
    {
        _configStore = configStore;
        _credentials = new HttpCredentialManager(configStore);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var configuration = _configStore.GetOrCreate();
        if (!configuration.HttpServiceEnabled)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await StartUnsafeAsync(configuration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
            }
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
            var configuration = _configStore.ConfigureHttpService(request.BindMode, request.Port);
            _lastError = null;
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
            var configuration = _configStore.GetOrCreate();
            if (string.Equals(configuration.HttpBindMode, HttpBindModes.All, StringComparison.Ordinal)
                && !confirmRemote)
            {
                throw new HttpAdministrationException(
                    ErrorCodes.ConfirmationRequired,
                    "Binding MCP HTTP to 0.0.0.0 requires explicit confirmation because bearer tokens use plaintext HTTP.");
            }

            configuration = _configStore.SetHttpServiceEnabled(true);
            if (_server is null)
            {
                try
                {
                    await StartUnsafeAsync(configuration, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
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
        if (_server is not null)
        {
            return;
        }

        ConfigStore.ValidateBindMode(configuration.HttpBindMode);
        ConfigStore.ValidatePort(configuration.HttpPort);
        _connections = new HttpConnectionRegistry();
        var address = string.Equals(configuration.HttpBindMode, HttpBindModes.All, StringComparison.Ordinal)
            ? IPAddress.Any
            : IPAddress.Loopback;
        _server = await McpHost.StartHttpAsync(
            address,
            configuration.HttpPort,
            _credentials,
            _connections,
            cancellationToken).ConfigureAwait(false);
        _lastError = null;
    }

    private async Task StopUnsafeAsync()
    {
        if (_server is null)
        {
            return;
        }

        var server = _server;
        _server = null;
        await server.DisposeAsync().ConfigureAwait(false);
        _connections = new HttpConnectionRegistry();
    }

    private HttpServiceStatus CreateStatus(HostConfiguration configuration)
    {
        var bindAddress = string.Equals(configuration.HttpBindMode, HttpBindModes.All, StringComparison.Ordinal)
            ? "0.0.0.0"
            : "127.0.0.1";
        return HttpEndpointResolver.Populate(new HttpServiceStatus
        {
            Enabled = configuration.HttpServiceEnabled,
            Running = _server is not null,
            BindMode = configuration.HttpBindMode,
            BindAddress = bindAddress,
            Port = configuration.HttpPort,
            Endpoint = $"http://{bindAddress}:{configuration.HttpPort}/mcp",
            ActiveConnectionCount = _connections.Count,
            LastError = _lastError
        });
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
