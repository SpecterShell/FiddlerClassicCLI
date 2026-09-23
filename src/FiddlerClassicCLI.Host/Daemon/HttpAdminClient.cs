// Provides managed HTTP administration through the daemon with safe offline configuration fallback.
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Daemon;

internal sealed class HttpAdminClient
{
    private readonly DaemonClient _daemonClient;
    private readonly ConfigStore _configStore;

    public HttpAdminClient(DaemonClient daemonClient, ConfigStore configStore)
    {
        _daemonClient = daemonClient;
        _configStore = configStore;
    }

    public async Task<HttpServiceStatus> GetServiceStatusAsync(CancellationToken cancellationToken)
    {
        var daemon = await _daemonClient.TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (daemon is null)
        {
            using var ownership = _daemonClient.AcquireOfflineAccess();
            return HttpEndpointResolver.FromConfiguration(_configStore.GetOrCreate());
        }

        if (!SupportsManagedHttp(daemon))
        {
            var status = HttpEndpointResolver.FromConfiguration(_configStore.GetOrCreate());
            status.LastError = "The running CLI daemon must be restarted to manage MCP HTTP.";
            return status;
        }

        return daemon.HttpService
            ?? await _daemonClient.GetHttpServiceStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates listener settings through the daemon or under exclusive offline ownership.</summary>
    /// <param name="request">The settings to change and any explicit security acknowledgment.</param>
    /// <param name="cancellationToken">Cancels daemon discovery and the configuration exchange.</param>
    public async Task<HttpServiceStatus> ConfigureServiceAsync(
        ConfigureHttpServiceRequest request,
        CancellationToken cancellationToken)
    {
        var daemon = await _daemonClient.TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (daemon is null)
        {
            using var ownership = _daemonClient.AcquireOfflineAccess();
            return HttpEndpointResolver.FromConfiguration(_configStore.ConfigureHttpService(request));
        }

        EnsureManagedHttp(daemon);
        return await _daemonClient.ConfigureHttpServiceAsync(
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<HttpServiceStatus> EnableServiceAsync(bool confirm, CancellationToken cancellationToken)
    {
        var daemon = await _daemonClient.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        EnsureManagedHttp(daemon);
        return await _daemonClient.EnableHttpServiceAsync(confirm, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HttpServiceStatus> DisableServiceAsync(bool confirm, CancellationToken cancellationToken)
    {
        var daemon = await _daemonClient.TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (daemon is null)
        {
            using var ownership = _daemonClient.AcquireOfflineAccess();
            return HttpEndpointResolver.FromConfiguration(_configStore.SetHttpServiceEnabled(false));
        }

        EnsureManagedHttp(daemon);
        return await _daemonClient.DisableHttpServiceAsync(confirm, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ListHttpClientsResponse> ListClientsAsync(CancellationToken cancellationToken)
    {
        var daemon = await _daemonClient.TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (daemon is not null)
        {
            EnsureManagedHttp(daemon);
            return await _daemonClient.ListHttpClientsAsync(cancellationToken).ConfigureAwait(false);
        }

        using var ownership = _daemonClient.AcquireOfflineAccess();
        return ClientsFromConfiguration(_configStore.GetOrCreate());
    }

    public async Task<AuthorizeHttpClientResponse> AuthorizeClientAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var daemon = await _daemonClient.TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (daemon is not null)
        {
            EnsureManagedHttp(daemon);
            return await _daemonClient.AuthorizeHttpClientAsync(name, cancellationToken).ConfigureAwait(false);
        }

        using var ownership = _daemonClient.AcquireOfflineAccess();
        var secret = _configStore.AuthorizeClient(name);
        return new AuthorizeHttpClientResponse
        {
            Client = ToClient(secret.Client),
            Token = secret.Token
        };
    }

    public async Task<DeauthorizeHttpClientResponse> DeauthorizeClientAsync(
        string clientId,
        bool confirm,
        CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            throw new HttpAdministrationException(
                ErrorCodes.ConfirmationRequired,
                "Deauthorizing an HTTP client requires explicit confirmation.");
        }

        var daemon = await _daemonClient.TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (daemon is not null)
        {
            EnsureManagedHttp(daemon);
            return await _daemonClient.DeauthorizeHttpClientAsync(clientId, confirm, cancellationToken).ConfigureAwait(false);
        }

        using var ownership = _daemonClient.AcquireOfflineAccess();
        var result = _configStore.DeauthorizeClient(clientId);
        return new DeauthorizeHttpClientResponse
        {
            ClientId = result.ClientId,
            DefaultTokenRotated = result.DefaultTokenRotated
        };
    }

    public async Task<ListHttpConnectionsResponse> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        var daemon = await _daemonClient.TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (daemon is null)
        {
            using var ownership = _daemonClient.AcquireOfflineAccess();
            return new ListHttpConnectionsResponse();
        }

        EnsureManagedHttp(daemon);
        return await _daemonClient.ListHttpConnectionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DisconnectHttpConnectionResponse> DisconnectAsync(
        string connectionId,
        bool confirm,
        CancellationToken cancellationToken)
    {
        var daemon = await _daemonClient.TryGetStatusAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new DaemonClientException(ErrorCodes.NotFound, "No managed MCP HTTP connections are active.");
        EnsureManagedHttp(daemon);
        return await _daemonClient.DisconnectHttpConnectionAsync(connectionId, confirm, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool SupportsManagedHttp(DaemonStatus status)
    {
        return status.Capabilities.Contains(DaemonProtocol.ManagedHttpCapability, StringComparer.Ordinal);
    }

    private static void EnsureManagedHttp(DaemonStatus status)
    {
        if (!SupportsManagedHttp(status))
        {
            throw new DaemonClientException(
                ErrorCodes.ProtocolMismatch,
                "The running CLI daemon does not support the current MCP HTTP settings. Stop and restart the daemon.");
        }
    }

    private static ListHttpClientsResponse ClientsFromConfiguration(HostConfiguration configuration)
    {
        var clients = new List<AuthorizedHttpClientDto>
        {
            new()
            {
                ClientId = HttpClientIds.Default,
                Name = "Default CLI token",
                IsDefault = true,
                CreatedAtUtc = configuration.HttpDefaultTokenCreatedAtUtc
            }
        };
        clients.AddRange(configuration.AuthorizedHttpClients.Select(ToClient));
        return new ListHttpClientsResponse { Clients = clients };
    }

    private static AuthorizedHttpClientDto ToClient(AuthorizedHttpClientConfiguration client)
    {
        return new AuthorizedHttpClientDto
        {
            ClientId = client.ClientId,
            Name = client.Name,
            CreatedAtUtc = client.CreatedAtUtc
        };
    }
}
