// Snapshots bridge listener metadata for the management UI without exposing pipe streams.
namespace FiddlerClassicCLI.Bridge;

internal enum BridgeListenerState
{
    Stopped,
    Starting,
    Listening,
    Retrying,
    Unavailable
}

internal sealed class BridgePipeStatus
{
    /// <summary>Creates an immutable listener snapshot safe to read on the UI thread.</summary>
    /// <param name="pipeName">The actual server pipe name, without the Windows pipe prefix.</param>
    /// <param name="state">The current listener lifecycle state.</param>
    /// <param name="error">An operational startup or listener error, without request data.</param>
    public BridgePipeStatus(string pipeName, BridgeListenerState state, string? error = null)
    {
        PipeName = pipeName;
        State = state;
        Error = error;
    }

    public string PipeName { get; }
    public BridgeListenerState State { get; }
    public string? Error { get; }
}
