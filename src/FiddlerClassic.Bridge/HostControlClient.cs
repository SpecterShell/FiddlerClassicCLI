// Starts the configured host daemon and exchanges managed HTTP control requests over its user-only pipe.
using System.Diagnostics;
using System.IO.Pipes;
using System.Web.Script.Serialization;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge;

internal interface IHostControlClient
{
    HostLaunchRecord? ReadLaunchRecord();
    Task<DaemonStatus> EnsureStartedAsync(CancellationToken cancellationToken);
    Task<DaemonStatus> GetDaemonStatusAsync(CancellationToken cancellationToken);
    Task<HttpServiceStatus> GetServiceStatusAsync(CancellationToken cancellationToken);
    Task<HttpServiceStatus> ConfigureServiceAsync(string bindMode, int port, CancellationToken cancellationToken);
    Task<HttpServiceStatus> EnableServiceAsync(bool confirm, CancellationToken cancellationToken);
    Task<HttpServiceStatus> DisableServiceAsync(bool confirm, CancellationToken cancellationToken);
    Task<ListHttpClientsResponse> ListClientsAsync(CancellationToken cancellationToken);
    Task<AuthorizeHttpClientResponse> AuthorizeClientAsync(string name, CancellationToken cancellationToken);
    Task<DeauthorizeHttpClientResponse> DeauthorizeClientAsync(string clientId, CancellationToken cancellationToken);
    Task<ListHttpConnectionsResponse> ListConnectionsAsync(CancellationToken cancellationToken);
    Task<DisconnectHttpConnectionResponse> DisconnectAsync(string connectionId, CancellationToken cancellationToken);
}

internal sealed class HostControlClient : IHostControlClient
{
    private const int ConnectTimeoutMilliseconds = 1000;
    private readonly string _pipeName;
    private readonly TimeSpan _requestTimeout;

    public HostControlClient()
        : this(DaemonPipeNames.ForCurrentUser(), TimeSpan.FromSeconds(10))
    {
    }

    /// <summary>Creates a control client with an isolated pipe and exchange deadline.</summary>
    /// <param name="pipeName">The daemon control pipe to connect to.</param>
    /// <param name="requestTimeout">Bounds connection, request writing, and response reading together.</param>
    internal HostControlClient(string pipeName, TimeSpan requestTimeout)
    {
        _pipeName = pipeName;
        _requestTimeout = requestTimeout;
    }

    private static JavaScriptSerializer CreateSerializer() => new JavaScriptSerializer
    {
        MaxJsonLength = ProtocolConstants.MaxFrameBytes,
        RecursionLimit = 64
    };

    public HostLaunchRecord? ReadLaunchRecord()
    {
        var path = LaunchRecordPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return CreateSerializer().Deserialize<HostLaunchRecord>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Discovers or starts the daemon off the UI thread within ten seconds.</summary>
    /// <param name="cancellationToken">Cancels startup and all discovery requests on extension unload.</param>
    public async Task<DaemonStatus> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                return await Task.Run(() => EnsureStartedCoreAsync(timeout.Token), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out starting the Fiddler Classic CLI daemon. Check 'fiddler-classic daemon status'.");
            }
        }
    }

    private async Task<DaemonStatus> EnsureStartedCoreAsync(CancellationToken cancellationToken)
    {
        var launch = RequireLaunchRecord(ReadLaunchRecord());
        var existing = await TryGetDaemonStatusAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureCapability(existing);
            return existing;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launch.HostExecutablePath,
            Arguments = "daemon start",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(launch.HostExecutablePath)
        };
        using (var process = Process.Start(startInfo))
        {
            if (process is null)
            {
                throw new InvalidOperationException("The Fiddler Classic CLI daemon could not be started.");
            }
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await TryGetDaemonStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status is not null)
            {
                EnsureCapability(status);
                return status;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out starting the Fiddler Classic CLI daemon.");
    }

    public Task<HttpServiceStatus> GetServiceStatusAsync(CancellationToken cancellationToken)
    {
        return SendAsync<EmptyRequest, HttpServiceStatus>(
            DaemonProtocol.HttpServiceStatus,
            new EmptyRequest(),
            cancellationToken);
    }

    public Task<DaemonStatus> GetDaemonStatusAsync(CancellationToken cancellationToken)
    {
        return SendAsync<EmptyRequest, DaemonStatus>(DaemonProtocol.Status, new EmptyRequest(), cancellationToken);
    }

    public Task<HttpServiceStatus> ConfigureServiceAsync(
        string bindMode,
        int port,
        CancellationToken cancellationToken)
    {
        return SendAsync<ConfigureHttpServiceRequest, HttpServiceStatus>(
            DaemonProtocol.ConfigureHttpService,
            new ConfigureHttpServiceRequest { BindMode = bindMode, Port = port },
            cancellationToken);
    }

    public Task<HttpServiceStatus> EnableServiceAsync(bool confirm, CancellationToken cancellationToken)
    {
        return SendAsync<SetHttpServiceEnabledRequest, HttpServiceStatus>(
            DaemonProtocol.EnableHttpService,
            new SetHttpServiceEnabledRequest { Confirm = confirm },
            cancellationToken);
    }

    public Task<HttpServiceStatus> DisableServiceAsync(bool confirm, CancellationToken cancellationToken)
    {
        return SendAsync<SetHttpServiceEnabledRequest, HttpServiceStatus>(
            DaemonProtocol.DisableHttpService,
            new SetHttpServiceEnabledRequest { Confirm = confirm },
            cancellationToken);
    }

    public Task<ListHttpClientsResponse> ListClientsAsync(CancellationToken cancellationToken)
    {
        return SendAsync<EmptyRequest, ListHttpClientsResponse>(
            DaemonProtocol.ListHttpClients,
            new EmptyRequest(),
            cancellationToken);
    }

    public Task<AuthorizeHttpClientResponse> AuthorizeClientAsync(string name, CancellationToken cancellationToken)
    {
        return SendAsync<AuthorizeHttpClientRequest, AuthorizeHttpClientResponse>(
            DaemonProtocol.AuthorizeHttpClient,
            new AuthorizeHttpClientRequest { Name = name },
            cancellationToken);
    }

    public Task<DeauthorizeHttpClientResponse> DeauthorizeClientAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        return SendAsync<DeauthorizeHttpClientRequest, DeauthorizeHttpClientResponse>(
            DaemonProtocol.DeauthorizeHttpClient,
            new DeauthorizeHttpClientRequest { ClientId = clientId, Confirm = true },
            cancellationToken);
    }

    public Task<ListHttpConnectionsResponse> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        return SendAsync<EmptyRequest, ListHttpConnectionsResponse>(
            DaemonProtocol.ListHttpConnections,
            new EmptyRequest(),
            cancellationToken);
    }

    public Task<DisconnectHttpConnectionResponse> DisconnectAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        return SendAsync<DisconnectHttpConnectionRequest, DisconnectHttpConnectionResponse>(
            DaemonProtocol.DisconnectHttpConnection,
            new DisconnectHttpConnectionRequest { ConnectionId = connectionId, Confirm = true },
            cancellationToken);
    }

    private async Task<DaemonStatus?> TryGetDaemonStatusAsync(CancellationToken cancellationToken)
    {
        var connected = false;
        try
        {
            return await SendAsync<EmptyRequest, DaemonStatus>(
                DaemonProtocol.Status,
                new EmptyRequest(),
                cancellationToken,
                onConnected: () => connected = true).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!connected)
        {
            return null;
        }
    }

    /// <summary>Exchanges one correlated control envelope within a single cancellable deadline.</summary>
    /// <param name="method">The daemon operation to invoke.</param>
    /// <param name="payload">The bounded operation parameters.</param>
    /// <param name="cancellationToken">Cancels the exchange without reporting a timeout.</param>
    /// <param name="onConnected">Records that an existing daemon was reached during discovery.</param>
    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string method,
        TRequest payload,
        CancellationToken cancellationToken,
        Action? onConnected = null)
    {
        var serializer = CreateSerializer();
        var request = new DaemonRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Method = method,
            PayloadJson = serializer.Serialize(payload)
        };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var pipe = new NamedPipeClientStream(
                   ".",
                   _pipeName,
                   PipeDirection.InOut,
                   PipeOptions.Asynchronous))
        {
            timeout.CancelAfter(_requestTimeout);
            // Disposing also interrupts pending Framework pipe I/O that ignores cancellation after dispatch.
            using var abortOnCancellation = timeout.Token.Register(pipe.Dispose);
            try
            {
                await pipe.ConnectAsync(ConnectTimeoutMilliseconds, timeout.Token).ConfigureAwait(false);
                onConnected?.Invoke();
                await FrameCodec.WriteAsync(pipe, serializer.Serialize(request), timeout.Token).ConfigureAwait(false);
                var responseJson = await FrameCodec.ReadAsync(pipe, timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("The CLI daemon closed the control pipe without a response.");
                var response = serializer.Deserialize<DaemonResponse>(responseJson)
                    ?? throw new InvalidDataException("The CLI daemon returned an invalid response.");
                if (response.ProtocolVersion != DaemonProtocol.Version)
                {
                    throw new InvalidOperationException(
                        $"CLI daemon protocol {response.ProtocolVersion} is unsupported; expected {DaemonProtocol.Version}.");
                }

                if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The CLI daemon response did not match the request.");
                }

                if (!response.Success)
                {
                    throw new InvalidOperationException(response.Error?.Message ?? "The CLI daemon operation failed.");
                }

                timeout.Token.ThrowIfCancellationRequested();
                return serializer.Deserialize<TResponse>(response.PayloadJson)
                    ?? throw new InvalidDataException("The CLI daemon returned an empty response payload.");
            }
            catch (Exception exception) when (timeout.IsCancellationRequested
                && exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    "Timed out waiting for the CLI daemon. Check status before retrying; the operation may have completed.",
                    exception);
            }
        }
    }

    internal static void EnsureCapability(DaemonStatus status)
    {
        if (!status.Capabilities.Contains(DaemonProtocol.ManagedHttpCapability, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The running CLI daemon predates managed MCP HTTP support. Run 'fiddler-classic daemon stop' and restart Fiddler.");
        }
    }

    private static HostLaunchRecord RequireLaunchRecord(HostLaunchRecord? launch)
    {
        if (launch is null
            || string.IsNullOrWhiteSpace(launch.HostExecutablePath)
            || !Path.IsPathRooted(launch.HostExecutablePath)
            || !File.Exists(launch.HostExecutablePath))
        {
            throw new InvalidOperationException(
                "The Fiddler Classic CLI host was not found. Run 'fiddler-classic bridge install' and restart Fiddler.");
        }

        return launch;
    }

    private static string LaunchRecordPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FiddlerClassicCLI",
            "bridge-host.json");
    }
}
