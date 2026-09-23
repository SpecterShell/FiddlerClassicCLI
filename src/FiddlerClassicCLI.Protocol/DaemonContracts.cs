// Defines the additive daemon control protocol shared by the host and Fiddler extension.
namespace FiddlerClassicCLI.Protocol;

public static class DaemonProtocol
{
    public const int Version = 1;
    public const string Relay = "bridge.relay";
    public const string Status = "daemon.status";
    public const string Stop = "daemon.stop";
    public const string HttpServiceStatus = "http.service.status";
    public const string ConfigureHttpService = "http.service.configure";
    public const string EnableHttpService = "http.service.enable";
    public const string DisableHttpService = "http.service.disable";
    public const string ListHttpClients = "http.clients.list";
    public const string AuthorizeHttpClient = "http.clients.authorize";
    public const string DeauthorizeHttpClient = "http.clients.deauthorize";
    public const string ListHttpConnections = "http.connections.list";
    public const string DisconnectHttpConnection = "http.connections.disconnect";
    public const string ManagedHttpCapability = "managed-http-v1";
}

public static class HttpBindModes
{
    public const string Loopback = "loopback";
    public const string All = "all";
}

public sealed class DaemonRequest
{
    public int ProtocolVersion { get; set; } = DaemonProtocol.Version;
    public string RequestId { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
}

public sealed class DaemonResponse
{
    public int ProtocolVersion { get; set; } = DaemonProtocol.Version;
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public BridgeError? Error { get; set; }
}

public sealed class DaemonStatus
{
    public bool Running { get; set; }
    public int ProcessId { get; set; }
    public string StartedAtUtc { get; set; } = string.Empty;
    public string PipeName { get; set; } = string.Empty;
    public string HostVersion { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = Array.Empty<string>();
    public HttpServiceStatus? HttpService { get; set; }
}

public sealed class DaemonStopResult
{
    public bool WasRunning { get; set; }
}

public sealed class HttpServiceStatus
{
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    public string BindMode { get; set; } = HttpBindModes.Loopback;
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8877;
    public string Endpoint { get; set; } = "http://127.0.0.1:8877/mcp";
    // Client address hints are separate from Endpoint, which preserves the listener's bind URL.
    public string LoopbackEndpoint { get; set; } = string.Empty;
    public string[] LanEndpoints { get; set; } = Array.Empty<string>();
    public int ActiveConnectionCount { get; set; }
    public string? LastError { get; set; }
}

public sealed class ConfigureHttpServiceRequest
{
    public string? BindMode { get; set; }
    public int? Port { get; set; }
}

public sealed class SetHttpServiceEnabledRequest
{
    public bool Confirm { get; set; }
}

public sealed class ListHttpClientsResponse
{
    public List<AuthorizedHttpClientDto> Clients { get; set; } = new List<AuthorizedHttpClientDto>();
}

public sealed class AuthorizedHttpClientDto
{
    public string ClientId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string? LastSeenAtUtc { get; set; }
    public int ActiveConnectionCount { get; set; }
}

public sealed class AuthorizeHttpClientRequest
{
    public string Name { get; set; } = string.Empty;
}

public sealed class AuthorizeHttpClientResponse
{
    public AuthorizedHttpClientDto Client { get; set; } = new AuthorizedHttpClientDto();
    public string Token { get; set; } = string.Empty;
}

public sealed class DeauthorizeHttpClientRequest
{
    public string ClientId { get; set; } = string.Empty;
    public bool Confirm { get; set; }
}

public sealed class DeauthorizeHttpClientResponse
{
    public string ClientId { get; set; } = string.Empty;
    public bool DefaultTokenRotated { get; set; }
    public int DisconnectedConnectionCount { get; set; }
}

public sealed class ListHttpConnectionsResponse
{
    public List<HttpConnectionDto> Connections { get; set; } = new List<HttpConnectionDto>();
}

public sealed class HttpConnectionDto
{
    public string ConnectionId { get; set; } = string.Empty;
    public string RemoteEndpoint { get; set; } = string.Empty;
    public string AuthorizationState { get; set; } = "pending";
    public string[] ClientIds { get; set; } = Array.Empty<string>();
    public string[] ClientNames { get; set; } = Array.Empty<string>();
    public string ConnectedAtUtc { get; set; } = string.Empty;
    public string LastActivityAtUtc { get; set; } = string.Empty;
    public long TotalRequestCount { get; set; }
    public int ActiveRequestCount { get; set; }
}

public sealed class DisconnectHttpConnectionRequest
{
    public string ConnectionId { get; set; } = string.Empty;
    public bool Confirm { get; set; }
}

public sealed class DisconnectHttpConnectionResponse
{
    public string ConnectionId { get; set; } = string.Empty;
    public bool Disconnected { get; set; }
}

public sealed class HostLaunchRecord
{
    public string HostExecutablePath { get; set; } = string.Empty;
    public string HostVersion { get; set; } = string.Empty;
}
