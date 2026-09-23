// Defines the explicit metadata allowlist used by diagnostic exports, separate from transport DTOs.
namespace FiddlerClassicCLI.Host.Services;

internal sealed class DiagnosticReport
{
    public int SchemaVersion { get; init; } = 1;
    public string? HostVersion { get; init; }
    public int ExpectedBridgeProtocolVersion { get; init; }
    public int ExpectedDaemonProtocolVersion { get; init; }
    public DiagnosticFiddlerStatus? Fiddler { get; init; }
    public DiagnosticDaemonStatus Daemon { get; init; } = new();
    public IReadOnlyList<DiagnosticError> Errors { get; init; } = [];
}

internal sealed class DiagnosticFiddlerStatus
{
    public bool Installed { get; init; }
    public bool Running { get; init; }
    public string? DiscoveredVersion { get; init; }
    public bool BridgeInstalled { get; init; }
    public bool BridgeConnected { get; init; }
    public string? BridgeVersion { get; init; }
}

internal sealed class DiagnosticDaemonStatus
{
    public bool? Running { get; init; }
    public string? HostVersion { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public DiagnosticListenerStatus Listener { get; init; } = new();
}

internal sealed class DiagnosticListenerStatus
{
    public bool? Enabled { get; init; }
    public bool? Running { get; init; }
    public string? BindMode { get; init; }
    public string? BindAddress { get; init; }
    public int? Port { get; init; }
}

internal sealed record DiagnosticError(string Component, string Code, string Message);

// The caller may print this receipt. The destination path is never included in the report itself.
internal sealed record DiagnosticExportResult(string Path, string Format = "json");
