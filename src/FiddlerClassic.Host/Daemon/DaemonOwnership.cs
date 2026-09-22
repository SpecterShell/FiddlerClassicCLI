// Shares daemon ownership with offline administration so startup cannot race configuration fallback.
using System.IO.Pipes;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Daemon;

internal static class DaemonOwnership
{
    /// <summary>Claims the user-only ownership pipe, or returns null when absence cannot be established.</summary>
    /// <param name="pipeName">The daemon control pipe whose ownership is being claimed.</param>
    /// <returns>A lease that must remain open for the daemon lifetime or the complete offline operation.</returns>
    public static NamedPipeServerStream? TryAcquire(string pipeName)
    {
        try
        {
            return new NamedPipeServerStream(
                DaemonPipeNames.OwnershipPipeName(pipeName),
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.FirstPipeInstance | PipeOptions.CurrentUserOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A busy or inaccessible owner is never evidence that the daemon has stopped.
            return null;
        }
    }
}
