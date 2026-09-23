// Separates Fiddler application lifecycle decisions from Windows process and installation access.
namespace FiddlerClassicCLI.Host.Services;

internal interface IFiddlerAppPlatform
{
    IReadOnlyList<FiddlerInstallation> FindInstallations(string? executablePath);
    IReadOnlyList<IFiddlerAppProcess> GetProcesses();
    IDisposable AcquireOperation();
    int Start(string executablePath);
}

internal interface IFiddlerAppProcess : IDisposable
{
    FiddlerAppProcessInfo Info { get; }
    bool HasExited { get; }
    bool CloseMainWindow();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed record FiddlerAppProcessInfo(int ProcessId, string ExecutablePath, string? Version, bool Supported);
internal sealed record FiddlerAppDetection(
    IReadOnlyList<FiddlerInstallation> Installations, IReadOnlyList<FiddlerAppProcessInfo> Processes);
internal sealed record FiddlerAppResult(string Action, bool Changed, bool Running, int? ProcessId, string? ExecutablePath);
