// Centralizes listener security acknowledgments before dispatching configuration or enable requests.
using System.Net;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel
{
    /// <summary>Confirms authentication removal and configurations that enable unsafe startup access.</summary>
    /// <param name="request">The captured configuration changes. Confirmation is set only after approval.</param>
    private bool ConfirmConfiguration(ConfigureHttpServiceRequest request)
    {
        var service = _lastService;
        if (service is null) return false;
        var authenticationMode = request.AuthenticationMode ?? service.AuthenticationMode;
        var remote = IsRemote(request.BindMode ?? service.BindMode, request.BindAddresses ?? service.BindAddresses);
        var startup = request.StartupMode ?? service.StartupMode;
        var removesAuthentication = request.AuthenticationMode == HttpAuthenticationModes.None;
        var unsafeStartup = startup == HttpStartupModes.Enabled && (remote || authenticationMode == HttpAuthenticationModes.None);
        var confirmed = false;
        if ((removesAuthentication || unsafeStartup) && !ConfirmAccess(authenticationMode, remote,
                unsafeStartup ? "MCP HTTP startup access" : "Disable MCP HTTP authentication", out confirmed)) return false;
        // The daemon checks its current configuration atomically. A stale safe snapshot must not
        // authorize a risk introduced by another caller after this panel's last refresh.
        request.Confirm = confirmed;
        return true;
    }

    private bool ConfirmAccess(string authenticationMode, bool remote, string title, out bool confirmed)
    {
        confirmed = false;
        if (authenticationMode == HttpAuthenticationModes.None)
            return confirmed = _confirm("MCP HTTP authentication is disabled. Anyone who can reach the listener has full access to captured traffic " +
                "and traffic-changing operations. Requests travel without encryption. Continue?", title);
        if (!remote) return true;
        return confirmed = _confirm("Remote MCP HTTP sends bearer credentials without encryption. " +
            "Anyone who observes the network can reuse them to access captured traffic and traffic-changing operations. Continue?", title);
    }

    private static bool IsRemote(string mode, string[] addresses) => mode == HttpBindModes.All
        || mode == HttpBindModes.Selected && addresses.Any(value => !IPAddress.TryParse(value, out var address) || !IPAddress.IsLoopback(address));

    private void UpdateEnabledCheck()
    {
        _updatingSettings = true;
        try { _enabled.Checked = IsServiceActive(_lastService); }
        finally { _updatingSettings = false; }
    }
}
