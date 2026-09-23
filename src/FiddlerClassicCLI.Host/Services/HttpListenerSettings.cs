// Validates bounded IPv4 listener selections and centralizes HTTP exposure checks.
using System.Net;
using System.Net.Sockets;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal static class HttpListenerSettings
{
    internal static void Validate(HostConfiguration configuration)
    {
        if (configuration.HttpBindMode is not (HttpBindModes.Loopback or HttpBindModes.All or HttpBindModes.Selected))
            throw Invalid("Bind mode must be 'loopback', 'all', or 'selected'.");
        if (configuration.HttpStartupMode is not (HttpStartupModes.Enabled or HttpStartupModes.Disabled or HttpStartupModes.LastState))
            throw Invalid("Startup mode must be 'enabled', 'disabled', or 'last-state'.");
        if (!HttpAuthenticationModes.IsValid(configuration.HttpAuthenticationMode))
            throw Invalid("Authentication mode must be 'required', 'non-loopback', or 'none'.");
        var addresses = configuration.HttpBindAddresses;
        if (addresses is null || addresses.Length > HttpListenerLimits.MaximumSelectedAddresses)
            throw Invalid($"Choose at most {HttpListenerLimits.MaximumSelectedAddresses} IPv4 listener addresses.");
        if (configuration.HttpBindMode != HttpBindModes.Selected && addresses.Length != 0)
            throw Invalid("Explicit addresses require the 'selected' bind mode.");
        if (configuration.HttpBindMode == HttpBindModes.Selected && addresses.Length == 0)
            throw Invalid("Choose at least one IPv4 address for the 'selected' bind mode.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in addresses)
        {
            if (!IsUnicastAddress(text) || !seen.Add(text))
                throw Invalid("Selected addresses must be unique dotted-decimal IPv4 unicast addresses.");
        }
    }

    internal static bool IsUnicastAddress(string? text)
    {
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || !string.Equals(address.ToString(), text, StringComparison.Ordinal)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] > 0 && bytes[0] < 224;
    }

    internal static IPAddress[] GetAddresses(HostConfiguration configuration) => configuration.HttpBindMode switch
    {
        HttpBindModes.All => [IPAddress.Any],
        HttpBindModes.Loopback => [IPAddress.Loopback],
        _ => configuration.HttpBindAddresses.Select(IPAddress.Parse).ToArray()
    };

    internal static bool RequiresEnableConfirmation(HostConfiguration configuration) =>
        configuration.HttpAuthenticationMode == HttpAuthenticationModes.None
        || GetAddresses(configuration).Any(address => !IPAddress.IsLoopback(address));

    internal static string ExposureWarning(HostConfiguration configuration) => configuration.HttpAuthenticationMode != HttpAuthenticationModes.None
        ? "Remote MCP HTTP access requires explicit confirmation. Bearer credentials travel without encryption and can be reused if observed on the network."
        : "Unauthenticated MCP HTTP access requires explicit confirmation. Any client that can reach the listener can read captured traffic and use every MCP action without a token.";

    private static HttpAdministrationException Invalid(string message) => new(ErrorCodes.InvalidRequest, message);
}
