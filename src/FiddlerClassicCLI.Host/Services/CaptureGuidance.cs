// Shares capture command guidance across CLI help and MCP tool descriptions.
namespace FiddlerClassicCLI.Host.Services;

internal static class CaptureGuidance
{
    private const string Details =
        " A running Fiddler proxy keeps receiving traffic explicitly sent to its port regardless of attachment. "
        + "Loopback clients use localhost or 127.0.0.1. When Fiddler listens on all interfaces (IPv4 0.0.0.0), "
        + "remote clients use a reachable host IP, subject to Fiddler remote-access, firewall, and network settings. "
        + "0.0.0.0 is a bind address. Fiddler's proxy listener and MCP HTTP bind are separate settings. "
        + "Certificates and HTTPS-decryption settings stay unchanged.";

    public const string Group = "Attach or detach Fiddler Classic as the host Windows system proxy only." + Details;
    public const string Start = "Attach Fiddler Classic as the host Windows system proxy only." + Details;
    public const string Stop = "Detach Fiddler Classic from the host Windows system proxy only." + Details;
}
