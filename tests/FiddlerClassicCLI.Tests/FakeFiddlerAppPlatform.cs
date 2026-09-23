// Supplies inert process leases for lifecycle and CLI tests without launching or closing real applications.
using FiddlerClassicCLI.Host.Services;

namespace FiddlerClassicCLI.Tests;

internal sealed class FakeFiddlerAppPlatform : IFiddlerAppPlatform
{
    internal const string DefaultPath = @"C:\Test Fiddler\Fiddler.exe";
    internal List<FiddlerInstallation> Installations { get; } = [new(DefaultPath, "6.0.20261.7291", true)];
    internal List<FakeFiddlerAppProcess> Processes { get; } = [];
    internal List<string> Events { get; } = [];
    internal Exception? StartError { get; set; }
    internal int Acquisitions { get; private set; }
    internal bool Locked { get; private set; }
    internal int Starts { get; private set; }
    internal string? StartedPath { get; private set; }

    public IReadOnlyList<FiddlerInstallation> FindInstallations(string? path) =>
        FiddlerEnvironment.FindInstallationsFromCandidates(Installations.Select(item => item.ExecutablePath),
            candidate => Installations.Any(item => item.ExecutablePath.Equals(candidate, StringComparison.OrdinalIgnoreCase)),
            candidate => Installations.Single(item => item.ExecutablePath.Equals(candidate, StringComparison.OrdinalIgnoreCase)).Version,
            path);

    public IReadOnlyList<IFiddlerAppProcess> GetProcesses() => Processes.Cast<IFiddlerAppProcess>().ToArray();

    public IDisposable AcquireOperation()
    {
        Assert.False(Locked);
        Locked = true;
        Acquisitions++;
        return new Lease(() => Locked = false);
    }

    public int Start(string path)
    {
        Assert.True(Locked);
        if (StartError is not null)
        {
            throw StartError;
        }
        Starts++;
        StartedPath = path;
        Events.Add("start");
        return 1234;
    }

    private sealed class Lease(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class FakeFiddlerAppProcess(int id = 42, string path = FakeFiddlerAppPlatform.DefaultPath) : IFiddlerAppProcess
{
    public FiddlerAppProcessInfo Info { get; set; } = new(id, path, "6.0.20261.7291", true);
    public bool HasExited { get; set; }
    internal bool AcceptClose { get; set; } = true;
    internal bool Disposed { get; private set; }
    internal int CloseRequests { get; private set; }
    internal Func<CancellationToken, Task>? Wait { get; set; }
    internal Action? OnClose { get; set; }

    public bool CloseMainWindow()
    {
        CloseRequests++;
        OnClose?.Invoke();
        return AcceptClose;
    }

    public Task WaitForExitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Wait is not null)
        {
            return Wait(token);
        }
        HasExited = true;
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}
