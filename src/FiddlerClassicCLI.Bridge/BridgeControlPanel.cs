// Presents MCP HTTP controls, named-pipe diagnostics, and component versions in Fiddler.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel : UserControl
{
    private readonly IHostControlClient _client;
    private readonly BridgeHostLifetime? _hostLifetime;
    private readonly string _fiddlerVersion;
    private readonly Func<string, string, bool> _confirm;
    private readonly Action<string> _showError;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
    private readonly SelectableAddress _endpoint = new SelectableAddress { AccessibleName = "Loopback endpoint" };
    private readonly LanEndpointList _lanEndpoints = new LanEndpointList();
    private readonly Label _serviceError = new Label { AutoSize = true, ForeColor = Color.DarkRed, AccessibleName = "Service error", UseMnemonic = false };
    private readonly Label _versions = new Label { AutoSize = true, AccessibleName = "Component versions", UseMnemonic = false };
    private readonly ContentSizedComboBox _bindMode = new ContentSizedComboBox { AccessibleName = "Listener bind mode" };
    private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 90, AccessibleName = "Listener port" };
    private readonly Button _apply = new PanelButton { Text = "&Apply", AutoSize = true, AccessibleName = "Apply listener settings" };
    private readonly CheckBox _enabled = new WrappingCheckBox { Text = "&Enable MCP HTTP", AccessibleName = "Enable MCP HTTP service", AccessibleDescription = "Not checked" };
    private readonly InterfaceAddressList _interfaces = new InterfaceAddressList();
    private readonly ContentSizedComboBox _startup = new ContentSizedComboBox { AccessibleName = "MCP startup mode" };
    private readonly ContentSizedComboBox _authenticationMode = new ContentSizedComboBox { AccessibleName = "MCP authentication mode" };
    private readonly Button _applyPreferences = new PanelButton { Text = "&Save settings", AutoSize = true, AccessibleName = "Save MCP settings" };
    private readonly Label _authenticationWarning = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Color.DarkRed, AccessibleName = "Authentication warning" };
    private readonly ClipboardButton _copyEndpoint = new ClipboardButton { AccessibleName = "Copy loopback endpoint" };
    private readonly Button _refresh = new PanelButton { Text = "&Refresh", AutoSize = true, AccessibleName = "Refresh service status" };
    private readonly Button _authorize = new PanelButton { Text = "A&uthorize client", AutoSize = true, AccessibleName = "Authorize HTTP client" };
    private readonly Button _deauthorize = new PanelButton { Text = "Deauthori&ze", AutoSize = true, AccessibleName = "Deauthorize selected HTTP client" };
    private readonly Button _rotateDefault = new PanelButton { Text = "Rotate default &token", AutoSize = true, AccessibleName = "Rotate default CLI token" };
    private readonly Button _disconnect = new PanelButton { Text = "Dis&connect", AutoSize = true, AccessibleName = "Disconnect selected HTTP connection" };
    private readonly StatusGrid _clients = new StatusGrid();
    private readonly StatusGrid _connections = new StatusGrid();
    private bool _busy;
    private bool _refreshing;
    private Task _refreshTask = Task.CompletedTask;
    private bool _settingsDirty;
    private bool _startupDirty;
    private bool _authenticationDirty;
    private bool _pendingEnabledChange;
    private bool _updatingSettings;
    private HttpServiceStatus? _lastService;

    /// <summary>Creates the management tab with injectable, nonblocking listener diagnostics.</summary>
    /// <param name="client">The daemon control client used for asynchronous IPC.</param>
    /// <param name="hostLifetime">Optional extension-owned daemon startup task.</param>
    /// <param name="fiddlerVersion">The loaded Fiddler version captured on its UI thread.</param>
    /// <param name="bridgeStatus">Returns the local listener snapshot without IPC or Fiddler API calls.</param>
    /// <param name="confirm">Optional modal confirmation boundary for isolated UI tests.</param>
    /// <param name="showError">Optional error-dialog boundary for isolated UI tests.</param>
    internal BridgeControlPanel(IHostControlClient client, BridgeHostLifetime? hostLifetime = null,
        string fiddlerVersion = "unknown", Func<BridgePipeStatus>? bridgeStatus = null,
        Func<string, string, bool>? confirm = null, Action<string>? showError = null)
    {
        _client = client;
        _hostLifetime = hostLifetime;
        _fiddlerVersion = fiddlerVersion;
        _confirm = confirm ?? ((message, title) => MessageBox.Show(this, message, title,
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK);
        _showError = showError ?? (message => MessageBox.Show(this, message, "Fiddler Classic CLI",
            MessageBoxButtons.OK, MessageBoxIcon.Error));
        _bridgeStatus = bridgeStatus ?? (() => new BridgePipeStatus(string.Empty, BridgeListenerState.Unavailable));
        AccessibleName = "Fiddler Classic CLI management";
        Dock = DockStyle.Fill;
        BuildLayout();
        InitializeToolTips();
        WireEvents();
        InitializePipeDiagnostics();
        SetActionButtons(true);
    }

    internal string ServiceStateText => _enabled.AccessibleDescription;

    internal string ServiceErrorText => _serviceError.Text;

    internal bool ServiceSettingsEnabled => _bindMode.Enabled && _port.Enabled && _apply.Enabled;

    internal int ClientRowCount => _clients.Rows.Count;

    internal int ConnectionRowCount => _connections.Rows.Count;
    internal string VersionText => _versions.Text;
    internal bool IsBusy => _busy || _refreshing;

    internal Task RefreshForTestingAsync()
    {
        return RefreshAsync(RefreshCoreAsync);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await RefreshAsync(async () =>
        {
            if (_hostLifetime is not null)
            {
                await _hostLifetime.Completion;
                if (_hostLifetime.Error is not null)
                {
                    throw new InvalidOperationException(_hostLifetime.Error);
                }
            }
            await RefreshCoreAsync();
        }, showErrors: false);
        if (!_lifetime.IsCancellationRequested)
        {
            _refreshTimer.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _buttonHints.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }
    private void SetActionButtons(bool enabled)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }
        _refresh.Enabled = enabled && !_refreshing;
        _refreshPipes.Enabled = enabled && !_refreshing;
        var hasStatus = enabled && _lastService is not null;
        var active = IsServiceActive(_lastService);
        _enabled.Enabled = hasStatus && (active || !_settingsDirty && !_authenticationDirty);
        var canManageCredentials = hasStatus && _lastService!.AuthenticationMode != HttpAuthenticationModes.None;
        _authorize.Enabled = canManageCredentials;
        _deauthorize.Enabled = canManageCredentials && SelectedId(_clients) is not null;
        _rotateDefault.Enabled = canManageCredentials;
        _disconnect.Enabled = hasStatus && SelectedId(_connections) is not null;
        // Copying known addresses is read-only and remains available during refreshes and mutations.
        _copyEndpoint.Enabled = !string.IsNullOrWhiteSpace(_endpoint.Text);
        _apply.Enabled = hasStatus;
        _applyPreferences.Enabled = hasStatus && (_startupDirty || _authenticationDirty && !active);
        _bindMode.Enabled = hasStatus;
        _port.Enabled = _bindMode.Enabled;
        _interfaces.Enabled = _bindMode.Enabled;
        _authenticationMode.Enabled = hasStatus && !active;
        _startup.Enabled = hasStatus;
    }

    private static bool IsServiceActive(HttpServiceStatus? service) => service?.Enabled == true || service?.Running == true;
    private static string? SelectedId(DataGridView grid)
    {
        return grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as string;
    }
}
