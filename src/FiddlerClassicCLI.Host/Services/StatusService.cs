// Combines local installation details with live bridge status probes.
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal sealed class StatusService
{
    private readonly IBridgeClient _bridgeClient;
    private readonly FiddlerEnvironment _environment;

    public StatusService(IBridgeClient bridgeClient, FiddlerEnvironment environment)
    {
        _bridgeClient = bridgeClient;
        _environment = environment;
    }

    public async Task<StatusResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        return (await ProbeAsync(cancellationToken).ConfigureAwait(false)).Status;
    }

    /// <summary>
    /// Combines local installation and process discovery with a live bridge probe when Fiddler is running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the live bridge request.</param>
    public async Task<StatusProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var fiddlerPath = _environment.FindFiddlerPath();
        using var process = _environment.FindRunningProcess();
        var localStatus = new StatusResponse
        {
            FiddlerInstalled = fiddlerPath is not null,
            FiddlerPath = fiddlerPath,
            FiddlerVersion = _environment.GetInstalledVersion(fiddlerPath),
            FiddlerRunning = process is not null,
            FiddlerProcessId = process?.Id,
            BridgeInstalled = _environment.IsBridgeInstalled()
        };

        if (process is null)
        {
            return new StatusProbe(localStatus, null, null);
        }

        try
        {
            var bridgeStatus = await _bridgeClient.SendAsync<EmptyRequest, StatusResponse>(
                Operations.GetStatus,
                new EmptyRequest(),
                cancellationToken).ConfigureAwait(false);
            bridgeStatus.FiddlerInstalled = localStatus.FiddlerInstalled;
            bridgeStatus.FiddlerPath = localStatus.FiddlerPath;
            bridgeStatus.FiddlerVersion = localStatus.FiddlerVersion ?? bridgeStatus.FiddlerVersion;
            bridgeStatus.BridgeInstalled = localStatus.BridgeInstalled;
            return new StatusProbe(bridgeStatus, null, null);
        }
        catch (BridgeClientException exception)
        {
            return new StatusProbe(localStatus, exception.Code, exception.Message);
        }
    }
}

internal sealed record StatusProbe(StatusResponse Status, string? BridgeErrorCode, string? BridgeErrorMessage);
