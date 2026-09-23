// Starts host discovery once per extension load, independently of tab visibility and native handles.
namespace FiddlerClassicCLI.Bridge;

internal sealed class BridgeHostLifetime : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    /// <summary>Begins bounded daemon discovery on a worker thread without creating a UI control.</summary>
    /// <param name="client">The host-control client shared with the management panel.</param>
    public BridgeHostLifetime(IHostControlClient client)
    {
        Completion = InitializeAsync(client, _lifetime.Token);
    }

    public Task Completion { get; }
    public string? Error { get; private set; }

    private async Task InitializeAsync(IHostControlClient client, CancellationToken cancellationToken)
    {
        try
        {
            var startup = await Task.Run(() => client.EnsureStartedAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            // A fresh daemon applies its policy during initialization. Applying it again could
            // overwrite a user's enable/disable action between startup and discovery completion.
            if (startup == HostStartResult.Reused)
                await client.ApplyStartupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Retain only an actionable error. A hidden tab must not leave a faulted, unobserved task.
            Error = exception.Message;
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
