// Shares capture command guidance across CLI help and MCP tool descriptions.
namespace FiddlerClassicCLI.Host.Services;

internal static class CaptureGuidance
{
    private const string Details = " A running Fiddler proxy keeps receiving traffic explicitly sent to its port regardless of attachment.";

    public const string Group = "Attach or detach Fiddler Classic as the host Windows system proxy only." + Details;
    public const string Start = "Attach Fiddler Classic as the host Windows system proxy only." + Details;
    public const string Stop = "Detach Fiddler Classic from the host Windows system proxy only." + Details;
}
