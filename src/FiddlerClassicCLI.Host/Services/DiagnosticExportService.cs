// Collects allowlisted diagnostic metadata without starting processes or exporting traffic or identity data.
using System.Text.Json;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Daemon;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal sealed class DiagnosticExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<CancellationToken, Task<StatusProbe>> _probeStatus;
    private readonly Func<CancellationToken, Task<DaemonStatus?>> _probeDaemon;

    /// <summary>Creates read-only probes, bypassing the daemon relay that can launch a stopped daemon.</summary>
    /// <param name="environment">Discovers installation and process metadata without changing configuration.</param>
    /// <param name="daemonClient">Provides only the non-starting daemon status operation.</param>
    public DiagnosticExportService(FiddlerEnvironment environment, DaemonClient daemonClient)
        : this(new StatusService(new NamedPipeBridgeClient(TimeSpan.FromSeconds(2)), environment).ProbeAsync,
            daemonClient.TryGetStatusAsync)
    {
    }

    /// <summary>Provides isolated probe seams. Neither delegate may start processes or read traffic.</summary>
    /// <param name="probeStatus">Reads local and bridge status, without using an auto-starting daemon relay.</param>
    /// <param name="probeDaemon">Reads daemon status, returning null for a confirmed stopped daemon.</param>
    internal DiagnosticExportService(
        Func<CancellationToken, Task<StatusProbe>> probeStatus,
        Func<CancellationToken, Task<DaemonStatus?>> probeDaemon)
    {
        _probeStatus = probeStatus ?? throw new ArgumentNullException(nameof(probeStatus));
        _probeDaemon = probeDaemon ?? throw new ArgumentNullException(nameof(probeDaemon));
    }

    /// <summary>Projects status into a closed metadata schema and replaces all remote error text.</summary>
    /// <param name="cancellationToken">Cancels the probes without converting cancellation into a diagnostic error.</param>
    /// <returns>A report containing no original transport objects, paths, traffic, or exception strings.</returns>
    public async Task<DiagnosticReport> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = new List<DiagnosticError>();
        DiagnosticFiddlerStatus? fiddler = null;
        var daemon = new DiagnosticDaemonStatus();
        try
        {
            var probe = await _probeStatus(cancellationToken).ConfigureAwait(false);
            var status = probe.Status;
            fiddler = new DiagnosticFiddlerStatus
            {
                Installed = status.FiddlerInstalled,
                Running = status.FiddlerRunning,
                // StatusService prefers the installed version over the live bridge's version.
                DiscoveredVersion = SanitizeVersion(status.FiddlerVersion),
                BridgeInstalled = status.BridgeInstalled,
                BridgeConnected = status.BridgeConnected,
                BridgeVersion = SanitizeVersion(status.BridgeVersion)
            };
            if (probe.BridgeErrorCode is not null || probe.BridgeErrorMessage is not null)
            {
                errors.Add(SanitizeError("bridge", probe.BridgeErrorCode));
            }
            else if (!status.FiddlerInstalled || !status.FiddlerRunning || !status.BridgeInstalled || !status.BridgeConnected)
            {
                errors.Add(SanitizeError("bridge", ErrorCodes.Unavailable));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            errors.Add(SanitizeError("status", GetErrorCode(exception)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var status = await _probeDaemon(cancellationToken).ConfigureAwait(false);
            if (status is null || !status.Running)
            {
                // Disabled and unconfigured are different: do not load/create credentials to fill these fields.
                daemon = new DiagnosticDaemonStatus
                {
                    Running = false,
                    Listener = new DiagnosticListenerStatus { Running = false }
                };
            }
            else
            {
                var managedHttp = status.Capabilities?.Contains(DaemonProtocol.ManagedHttpCapability, StringComparer.Ordinal) == true;
                daemon = new DiagnosticDaemonStatus
                {
                    Running = true,
                    HostVersion = SanitizeVersion(status.HostVersion),
                    Capabilities = managedHttp ? [DaemonProtocol.ManagedHttpCapability] : [],
                    Listener = ProjectListener(status.HttpService)
                };
                if (!managedHttp)
                {
                    errors.Add(SanitizeError("daemon", ErrorCodes.ProtocolMismatch));
                }
                if (status.HttpService is null || !string.IsNullOrEmpty(status.HttpService.LastError))
                {
                    errors.Add(SanitizeError("listener", ErrorCodes.Unavailable));
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            errors.Add(SanitizeError("daemon", GetErrorCode(exception)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new DiagnosticReport
        {
            HostVersion = typeof(DiagnosticExportService).Assembly.GetName().Version?.ToString(),
            ExpectedBridgeProtocolVersion = ProtocolConstants.Version,
            ExpectedDaemonProtocolVersion = DaemonProtocol.Version,
            Fiddler = fiddler,
            Daemon = daemon,
            Errors = errors.ToArray()
        };
    }

    /// <summary>Writes a new JSON file atomically without writing to stdout or replacing an existing destination.</summary>
    /// <param name="outputPath">An explicit absolute file path. Rejects stdout, devices, and alternate streams.</param>
    /// <param name="cancellationToken">Cancels probes and file writes before the completed file is published.</param>
    /// <returns>A separate receipt containing the destination path, never embedded in the diagnostic report.</returns>
    /// <exception cref="ArgumentException">The destination is not a regular absolute file path.</exception>
    /// <exception cref="IOException">The destination exists or the export cannot be completed.</exception>
    public async Task<DiagnosticExportResult> ExportAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        var destination = ValidateOutputPath(outputPath);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("The diagnostic destination already exists. Choose a new file path.");
        }

        var report = await CollectAsync(cancellationToken).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".fiddler-diagnostic-{Guid.NewGuid():N}.tmp");
        var ownsTemporary = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            ownsTemporary = false;
            return new DiagnosticExportResult(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Do not attach the original exception: its message and inner exceptions can contain user paths.
            throw new IOException("Could not create the diagnostic file. Choose a new file in an existing writable directory.");
        }
        finally
        {
            if (ownsTemporary)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Cleanup cannot overwrite the original failure. Any remaining file contains only allowlisted metadata.
                }
            }
        }
    }

    private static DiagnosticListenerStatus ProjectListener(HttpServiceStatus? status)
    {
        if (status is null) return new DiagnosticListenerStatus();
        return new DiagnosticListenerStatus
        {
            Enabled = status.Enabled,
            Running = status.Running,
            BindMode = status.BindMode is HttpBindModes.Loopback or HttpBindModes.All or HttpBindModes.Selected ? status.BindMode : null,
            BindAddress = status.BindAddress is "127.0.0.1" or "0.0.0.0" ? status.BindAddress : null,
            Port = status.Port is >= 1 and <= 65535 ? status.Port : null,
            StartupMode = status.StartupMode is HttpStartupModes.Enabled or HttpStartupModes.Disabled or HttpStartupModes.LastState
                ? status.StartupMode : null,
            AuthenticationMode = HttpAuthenticationModes.IsValid(status.AuthenticationMode) ? status.AuthenticationMode : null
        };
    }

    private static string? SanitizeVersion(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256) return null;
        // Export only numeric version components, never arbitrary prerelease/build metadata supplied by a peer.
        var end = value.AsSpan().IndexOfAny('-', '+');
        var numeric = end < 0 ? value : value[..end];
        return numeric.All(character => char.IsAsciiDigit(character) || character == '.')
            && Version.TryParse(numeric, out var version) ? version.ToString() : null;
    }

    private static string? GetErrorCode(Exception exception) => exception switch
    {
        BridgeClientException bridge => bridge.Code,
        DaemonClientException daemon => daemon.Code,
        TimeoutException or OperationCanceledException => ErrorCodes.Timeout,
        IOException or UnauthorizedAccessException => ErrorCodes.Unavailable,
        _ => ErrorCodes.Internal
    };

    private static DiagnosticError SanitizeError(string component, string? code)
    {
        var knownCode = code is ErrorCodes.InvalidRequest or ErrorCodes.ProtocolMismatch or ErrorCodes.NotFound
            or ErrorCodes.ConfirmationRequired or ErrorCodes.Conflict or ErrorCodes.Unavailable or ErrorCodes.Timeout
            ? code : ErrorCodes.Internal;
        var message = knownCode switch
        {
            ErrorCodes.ProtocolMismatch => "Install matching CLI and bridge versions, then restart the affected process explicitly.",
            ErrorCodes.Timeout => "Retry the diagnostic export and check that the affected process is responsive.",
            ErrorCodes.Unavailable when component == "listener" => "Check the MCP HTTP bind mode and port, then retry enabling the listener explicitly.",
            ErrorCodes.Unavailable when component == "daemon" => "Check daemon status and restart the daemon explicitly if needed.",
            ErrorCodes.Unavailable => "Check the Fiddler Classic installation and bridge, then start or restart Fiddler explicitly if needed.",
            ErrorCodes.InvalidRequest => "The status response was invalid. Verify that the installed CLI and bridge versions match.",
            ErrorCodes.Conflict => "The status probe encountered a conflict. Retry after the affected process finishes its current operation.",
            ErrorCodes.NotFound => "A required diagnostic component was not found. Verify the CLI and bridge installation.",
            ErrorCodes.ConfirmationRequired => "A read-only status probe unexpectedly required confirmation. Verify that the CLI and bridge versions match.",
            _ => "Diagnostic collection failed. Verify the installation and retry the export."
        };
        return new DiagnosticError(component, knownCode, message);
    }

    /// <summary>Requires a new regular file by rejecting device aliases and Windows path forms that can target streams.</summary>
    /// <param name="outputPath">The user-supplied destination, which is never included in validation failures.</param>
    /// <returns>A normalized absolute file path.</returns>
    private static string ValidateOutputPath(string outputPath)
    {
        const string message = "Diagnostic output must be an absolute regular file path. Stdout, devices, and alternate streams are rejected.";
        try
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !Path.IsPathFullyQualified(outputPath))
                throw new ArgumentException(message);
            var normalized = outputPath.Replace('/', '\\');
            if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) || normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException(message);
            // Validate the original components before GetFullPath can normalize trailing spaces or device aliases.
            var relative = normalized[Path.GetPathRoot(normalized)!.Length..];
            if (relative.Length == 0) throw new ArgumentException(message);
            foreach (var part in relative.Split('\\'))
            {
                if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException(message);
                var name = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$"
                    || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                        && (name[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')))
                    throw new ArgumentException(message);
            }
            return Path.GetFullPath(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(message, nameof(outputPath));
        }
    }
}
