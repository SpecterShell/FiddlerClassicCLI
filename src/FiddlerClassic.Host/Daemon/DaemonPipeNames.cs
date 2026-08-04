// Derives deterministic current-user pipe names for daemon communication and ownership.
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Daemon;

internal static class DaemonPipeNames
{
    private const string OverrideVariable = "FIDDLER_CLASSIC_DAEMON_PIPE_NAME";

    /// <summary>
    /// Returns a validated test override or a daemon name derived from the current user's bridge pipe.
    /// </summary>
    public static string ForCurrentUser()
    {
        var overrideName = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            Validate(overrideName, OverrideVariable);
            return overrideName;
        }

        return $"{PipeNames.ForCurrentUser()}.daemon.v{DaemonProtocol.Version}";
    }

    public static string OwnershipPipeName(string pipeName)
    {
        Validate(pipeName, nameof(pipeName));
        return $"{pipeName}.owner";
    }

    /// <summary>
    /// Rejects pipe names that exceed the local limit or contain unsafe characters.
    /// </summary>
    /// <param name="value">The pipe name to validate.</param>
    /// <param name="source">The setting name reported in validation errors.</param>
    private static void Validate(string value, string source)
    {
        if (value.Length > 200 || value.Any(character =>
                !char.IsLetterOrDigit(character) && character != '.' && character != '_' && character != '-'))
        {
            throw new InvalidOperationException(
                $"{source} may contain only letters, digits, periods, underscores, and hyphens.");
        }
    }
}
