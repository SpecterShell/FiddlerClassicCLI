// Derives the deterministic current-user pipe name used by the Fiddler bridge.
using System.Security.Cryptography;
using System.Text;

namespace FiddlerClassicCLI.Protocol;

public static class PipeNames
{
    /// <summary>
    /// Returns a validated test override or a deterministic pipe name derived from the current Windows identity.
    /// </summary>
    public static string ForCurrentUser()
    {
        var overrideName = Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_PIPE_NAME");
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            if (overrideName.Length > 200 || overrideName.Any(character =>
                    !char.IsLetterOrDigit(character) && character != '.' && character != '_' && character != '-'))
            {
                throw new InvalidOperationException(
                    "FIDDLER_CLASSIC_PIPE_NAME may contain only letters, digits, periods, underscores, and hyphens.");
            }

            return overrideName;
        }

        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        byte[] digest;

        using (var sha256 = SHA256.Create())
        {
            digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(identity));
        }

        var suffix = BitConverter.ToString(digest, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
        return $"fiddler-classic-cli.v{ProtocolConstants.Version}.{suffix}";
    }
}
