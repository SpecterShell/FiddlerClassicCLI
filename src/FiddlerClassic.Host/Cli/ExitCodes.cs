// Maps stable bridge and daemon failures to documented process exit codes.
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int NotInstalled = 3;
    public const int Unavailable = 4;
    public const int Rejected = 5;
    public const int Timeout = 6;

    /// <summary>
    /// Maps a stable bridge or daemon error code to its documented process exit code.
    /// </summary>
    /// <param name="code">The stable protocol error code.</param>
    public static int ForBridgeError(string code)
    {
        return code switch
        {
            ErrorCodes.Unavailable => Unavailable,
            ErrorCodes.Timeout => Timeout,
            ErrorCodes.NotFound or ErrorCodes.Conflict or ErrorCodes.ConfirmationRequired => Rejected,
            ErrorCodes.InvalidRequest or ErrorCodes.ProtocolMismatch => Usage,
            _ => Failure
        };
    }
}
