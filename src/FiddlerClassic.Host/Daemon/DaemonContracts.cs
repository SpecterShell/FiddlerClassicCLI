// Defines the framed request, response, and status contracts used by the CLI daemon.
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Daemon;

internal static class DaemonProtocol
{
    public const int Version = 1;
    public const string Relay = "bridge.relay";
    public const string Status = "daemon.status";
    public const string Stop = "daemon.stop";
}

internal sealed class DaemonRequest
{
    public int ProtocolVersion { get; set; } = DaemonProtocol.Version;
    public string RequestId { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
}

internal sealed class DaemonResponse
{
    public int ProtocolVersion { get; set; } = DaemonProtocol.Version;
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public BridgeError? Error { get; set; }
}

internal sealed class DaemonStatus
{
    public bool Running { get; set; }
    public int ProcessId { get; set; }
    public string StartedAtUtc { get; set; } = string.Empty;
    public string PipeName { get; set; } = string.Empty;
    public string HostVersion { get; set; } = string.Empty;
}

internal sealed class DaemonStopResult
{
    public bool WasRunning { get; set; }
}
