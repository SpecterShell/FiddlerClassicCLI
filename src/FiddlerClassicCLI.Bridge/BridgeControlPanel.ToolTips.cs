// Describes panel actions without including credentials or changing Fiddler-wide tooltip settings.
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel
{
    private readonly ToolTip _buttonHints = new ToolTip { ShowAlways = true };

    private void InitializeToolTips()
    {
        _buttonHints.SetToolTip(_apply, "Apply the listener addresses and port. An enabled service restarts with these settings.");
        _buttonHints.SetToolTip(_enabled, "Enable or disable MCP HTTP. Remote access, no-authentication mode, and disconnecting active clients require confirmation.");
        _buttonHints.SetToolTip(_interfaces, $"Select up to {HttpListenerLimits.MaximumSelectedAddresses} local IPv4 addresses. Saved addresses remain listed when their adapter is unavailable.");
        _buttonHints.SetToolTip(_startup, "Choose whether MCP HTTP starts enabled, disabled, or in its last saved state when Fiddler loads or the daemon starts.");
        _buttonHints.SetToolTip(_authenticationMode, "Require a bearer token for all connections, non-loopback connections only (default), or none. The loopback exemption requires loopback addresses at both ends. Disable the service before changing this setting.");
        _buttonHints.SetToolTip(_applyPreferences, "Save startup and authentication settings. Authentication removal and remote startup require confirmation.");
        _buttonHints.SetToolTip(_refresh, "Refresh service status, authorized clients, connections, and diagnostics.");
        _buttonHints.SetToolTip(_authorize, "Authorize a named client and display its new token once.");
        _buttonHints.SetToolTip(_deauthorize, "Revoke the selected client and disconnect its active connections. The default token is rotated.");
        _buttonHints.SetToolTip(_rotateDefault, "Replace the default CLI token and disconnect connections using it.");
        _buttonHints.SetToolTip(_disconnect, "Disconnect the selected transport connection. An operation already dispatched may have completed.");
        _buttonHints.SetToolTip(_refreshPipes, "Check named-pipe status without starting the daemon or changing listeners.");
        _copyEndpoint.ToolTipText = "Copy the loopback MCP HTTP URL to the clipboard.";
        _copyBridgePipe.ToolTipText = "Copy the full bridge named-pipe address to the clipboard.";
        _copyDaemonPipe.ToolTipText = "Copy the full daemon named-pipe address to the clipboard.";
    }

    internal string GetButtonToolTip(Control button) => button is ClipboardButton copy
        ? copy.ToolTipText : _buttonHints.GetToolTip(button);
}
