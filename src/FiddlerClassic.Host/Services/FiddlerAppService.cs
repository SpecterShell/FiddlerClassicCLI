// Implements explicit, confirmed Fiddler application lifecycle without bridge IPC or forced termination.
using System.ComponentModel;
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Services;

internal sealed class FiddlerAppService
{
    private readonly IFiddlerAppPlatform _platform;

    /// <summary>Uses the Windows backend by default; tests supply process leases with no native side effects.</summary>
    /// <param name="platform">Optional installation and process operations.</param>
    public FiddlerAppService(IFiddlerAppPlatform? platform = null)
    {
        if (platform is null)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Fiddler application commands require Windows.");
            }
            platform = new WindowsFiddlerAppPlatform();
        }
        _platform = platform;
    }

    public FiddlerAppDetection Detect(string? executablePath = null)
    {
        var installations = FindInstallations(executablePath);
        var processes = _platform.GetProcesses();
        try
        {
            return new(installations, processes.Select(process => process.Info).ToArray());
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>Starts Fiddler only if no target is running; successful launch does not imply bridge readiness.</summary>
    /// <param name="executablePath">An optional exact installation path; never a shell command.</param>
    /// <param name="cancellationToken">Checked before any process is launched.</param>
    public FiddlerAppResult Open(string? executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var ownership = _platform.AcquireOperation();
        var installations = FindInstallations(executablePath);
        var processes = _platform.GetProcesses();
        try
        {
            var target = SelectProcess(processes, null);
            if (target is not null)
            {
                RequireSupported(target.Info);
                RequireMatchingPath(target.Info, executablePath, installations);
                return new("open", false, true, target.Info.ProcessId, target.Info.ExecutablePath);
            }
            return Start("open", RequireInstallation(installations), cancellationToken);
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>Requests a normal close and optionally launches the same executable after verified exit.</summary>
    /// <param name="restart">Whether to reopen the target after it exits.</param>
    /// <param name="processId">Optional exact current-user process selection.</param>
    /// <param name="executablePath">Optional restart installation, mutually exclusive with processId.</param>
    /// <param name="timeoutSeconds">One to sixty seconds to wait for exit.</param>
    /// <param name="confirmed">Explicit permission to close Fiddler and risk unsaved captures.</param>
    /// <param name="cancellationToken">Cancels waiting; never kills Fiddler or starts a replacement on cancellation.</param>
    public async Task<FiddlerAppResult> CloseAsync(bool restart, int? processId, string? executablePath,
        int timeoutSeconds, bool confirmed, CancellationToken cancellationToken)
    {
        if (timeoutSeconds is < 1 or > 60 || processId is <= 0 ||
            (executablePath is not null && (!restart || processId.HasValue)))
        {
            throw new BridgeClientException(ErrorCodes.InvalidRequest, "Use a timeout of 1-60 seconds and a positive PID; --pid and --path cannot be combined.");
        }
        if (!confirmed)
        {
            throw new BridgeClientException(ErrorCodes.ConfirmationRequired, "Closing or restarting Fiddler requires confirmation. Save captures first, then use --yes.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var ownership = _platform.AcquireOperation();
        var processes = _platform.GetProcesses();
        try
        {
            var target = SelectProcess(processes, processId);
            var action = restart ? "restart" : "close";
            if (target is null)
            {
                return restart
                    ? Start(action, RequireInstallation(FindInstallations(executablePath)), cancellationToken)
                    : new(action, false, false, null, null);
            }

            RequireSupported(target.Info);
            // Validate the relaunch file before closing; do not discard a running instance
            // only to discover that its executable is missing or the selected path differs.
            var installation = restart ? RequireInstallation(FindInstallations(executablePath ?? target.Info.ExecutablePath)) : null;
            if (installation is not null)
            {
                RequireMatchingPath(target.Info, installation.ExecutablePath, [installation]);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!target.HasExited && !target.CloseMainWindow())
            {
                throw new BridgeClientException(ErrorCodes.Conflict,
                    "Fiddler cannot accept a normal close request. Resolve any open dialog in Fiddler and retry; the process was not force-closed.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await target.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BridgeClientException(ErrorCodes.Timeout,
                    "Fiddler has not exited. Save or dismiss any pending dialog and check app detect before retrying. No replacement was launched.");
            }

            return restart ? Start(action, installation!, cancellationToken)
                : new(action, true, false, target.Info.ProcessId, target.Info.ExecutablePath);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new BridgeClientException(ErrorCodes.Unavailable,
                "Fiddler could not complete the lifecycle operation. Check app detect and any Fiddler dialogs before retrying.", exception);
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    private IReadOnlyList<FiddlerInstallation> FindInstallations(string? path)
    {
        try
        {
            return _platform.FindInstallations(path);
        }
        catch (ArgumentException exception)
        {
            throw new BridgeClientException(ErrorCodes.InvalidRequest, exception.Message, exception);
        }
    }

    private static FiddlerInstallation RequireInstallation(IReadOnlyList<FiddlerInstallation> installations)
    {
        if (installations.Count == 0)
        {
            throw new BridgeClientException(ErrorCodes.Unavailable,
                "Fiddler Classic was not found. Install it separately or supply --path with an absolute Fiddler.exe path.");
        }
        return installations.FirstOrDefault(installation => installation.Supported)
            ?? throw new BridgeClientException(ErrorCodes.InvalidRequest, "Only Fiddler Classic 5.x and 6.x executables are supported.");
    }

    private static IFiddlerAppProcess? SelectProcess(IReadOnlyList<IFiddlerAppProcess> processes, int? processId)
    {
        if (processId.HasValue)
        {
            return processes.SingleOrDefault(process => process.Info.ProcessId == processId.Value)
                ?? throw new BridgeClientException(ErrorCodes.NotFound, "The PID is not an accessible Fiddler process owned by this user in this Windows session.");
        }
        if (processes.Count > 1)
        {
            throw new BridgeClientException(ErrorCodes.Conflict,
                "More than one Fiddler process is running. Use app detect, then close or restart one with --pid.");
        }
        return processes.SingleOrDefault();
    }

    private static void RequireSupported(FiddlerAppProcessInfo process)
    {
        if (!process.Supported)
        {
            throw new BridgeClientException(ErrorCodes.InvalidRequest, "The running executable could not be verified as Fiddler Classic 5.x or 6.x.");
        }
    }

    private static void RequireMatchingPath(FiddlerAppProcessInfo process, string? requestedPath,
        IReadOnlyList<FiddlerInstallation> installations)
    {
        if (requestedPath is not null && !string.Equals(process.ExecutablePath,
            RequireInstallation(installations).ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeClientException(ErrorCodes.Conflict, "A different Fiddler installation is running. Close it explicitly before opening the selected installation.");
        }
    }

    private FiddlerAppResult Start(string action, FiddlerInstallation installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return new(action, true, true, _platform.Start(installation.ExecutablePath), installation.ExecutablePath);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new BridgeClientException(ErrorCodes.Unavailable,
                "Windows could not launch Fiddler. Check the executable path and permissions. If restarting, Fiddler may already be closed.", exception);
        }
    }

    private static void DisposeProcesses(IReadOnlyList<IFiddlerAppProcess> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }
}
