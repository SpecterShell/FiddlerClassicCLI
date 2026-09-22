// Presents managed MCP HTTP status, client authorization, connections, and component versions in Fiddler.
using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge;

internal sealed partial class BridgeControlPanel : UserControl
{
    private readonly IHostControlClient _client;
    private readonly BridgeHostLifetime? _hostLifetime;
    private readonly string _fiddlerVersion;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
    private readonly Label _serviceState = new Label { AutoSize = true, AccessibleName = "Service status", UseMnemonic = false };
    private readonly Label _endpoint = new Label { AutoSize = true, AccessibleName = "Loopback endpoint", UseMnemonic = false };
    private readonly LanEndpointList _lanEndpoints = new LanEndpointList();
    private readonly Label _serviceError = new Label { AutoSize = true, ForeColor = Color.DarkRed, AccessibleName = "Service error", UseMnemonic = false };
    private readonly Label _versions = new Label { AutoSize = true, AccessibleName = "Component versions", UseMnemonic = false };
    private readonly ComboBox _bindMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = true, Width = 110, AccessibleName = "Listener bind mode" };
    private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 90, AccessibleName = "Listener port" };
    private readonly Button _apply = new Button { Text = "&Apply", AutoSize = true, AccessibleName = "Apply listener settings" };
    private readonly Button _toggle = new Button { Text = "&Enable", AutoSize = true, AccessibleName = "Enable MCP HTTP service" };
    private readonly Button _copyEndpoint = new Button { Text = "Copy &loopback", AutoSize = true, AccessibleName = "Copy loopback endpoint" };
    private readonly Button _refresh = new Button { Text = "&Refresh", AutoSize = true, AccessibleName = "Refresh service status" };
    private readonly Button _authorize = new Button { Text = "A&uthorize client", AutoSize = true, AccessibleName = "Authorize HTTP client" };
    private readonly Button _deauthorize = new Button { Text = "Deauthori&ze", AutoSize = true, AccessibleName = "Deauthorize selected HTTP client" };
    private readonly Button _rotateDefault = new Button { Text = "Rotate default &token", AutoSize = true, AccessibleName = "Rotate default CLI token" };
    private readonly Button _disconnect = new Button { Text = "Dis&connect", AutoSize = true, AccessibleName = "Disconnect selected HTTP connection" };
    private readonly DataGridView _clients = CreateGrid();
    private readonly DataGridView _connections = CreateGrid();
    private bool _busy;
    private bool _settingsDirty;
    private bool _updatingSettings;
    private HttpServiceStatus? _lastService;

    internal BridgeControlPanel(IHostControlClient client, BridgeHostLifetime? hostLifetime = null, string fiddlerVersion = "unknown")
    {
        _client = client;
        _hostLifetime = hostLifetime;
        _fiddlerVersion = fiddlerVersion;
        AccessibleName = "Fiddler Classic CLI management";
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BuildLayout();
        WireEvents();
        SetActionButtons(true);
    }

    internal string ServiceStateText => _serviceState.Text;

    internal string ServiceErrorText => _serviceError.Text;

    internal bool ServiceSettingsEnabled => _bindMode.Enabled && _port.Enabled && _apply.Enabled;

    internal int ClientRowCount => _clients.Rows.Count;

    internal int ConnectionRowCount => _connections.Rows.Count;
    internal string VersionText => _versions.Text;
    internal bool IsBusy => _busy;

    internal Task RefreshForTestingAsync()
    {
        return RunOperationAsync(RefreshCoreAsync, showErrors: false);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await RunOperationAsync(async () =>
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
            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }


    private void WireEvents()
    {
        _bindMode.SelectedIndexChanged += (_, _) => SettingsChanged();
        _port.ValueChanged += (_, _) => SettingsChanged();
        _port.TextChanged += (_, _) => SettingsChanged();
        _clients.SelectionChanged += (_, _) => SetActionButtons(!_busy);
        _connections.SelectionChanged += (_, _) => SetActionButtons(!_busy);
        _refreshTimer.Tick += async (_, _) => await RunOperationAsync(RefreshCoreAsync, showErrors: false);
        _refresh.Click += async (_, _) => await RunOperationAsync(RefreshCoreAsync);
        _apply.Click += async (_, _) => await RunOperationAsync(async () =>
        {
            var bindMode = _bindMode.SelectedItem?.ToString() ?? HttpBindModes.Loopback;
            var port = decimal.ToInt32(_port.Value);
            await _client.ConfigureServiceAsync(bindMode, port, _lifetime.Token);
            // Preserve any newer edits made while this request was in flight.
            if (_bindMode.SelectedItem?.ToString() == bindMode && _port.Value == port)
            {
                _settingsDirty = false;
            }
            await RefreshCoreAsync();
        });
        _toggle.Click += async (_, _) => await RunOperationAsync(ToggleServiceAsync);
        _copyEndpoint.Click += (_, _) =>
        {
            var endpoint = _endpoint.Text;
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                Clipboard.SetText(endpoint);
            }
        };
        _authorize.Click += async (_, _) => await RunOperationAsync(AuthorizeClientAsync);
        _deauthorize.Click += async (_, _) => await RunOperationAsync(DeauthorizeClientAsync);
        _rotateDefault.Click += async (_, _) => await RunOperationAsync(RotateDefaultTokenAsync);
        _disconnect.Click += async (_, _) => await RunOperationAsync(DisconnectAsync);
    }

    private void SettingsChanged()
    {
        if (!_updatingSettings)
        {
            _settingsDirty = true;
            SetActionButtons(!_busy);
        }
    }

    private async Task ToggleServiceAsync()
    {
        var service = _lastService ?? await _client.GetServiceStatusAsync(_lifetime.Token);
        if (service.Enabled)
        {
            var confirm = service.ActiveConnectionCount == 0 || MessageBox.Show(
                this,
                "Disabling MCP HTTP will disconnect active clients. An operation already dispatched may have completed.",
                "Disable MCP HTTP",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) == DialogResult.OK;
            if (!confirm)
            {
                return;
            }

            await _client.DisableServiceAsync(confirm: true, _lifetime.Token);
        }
        else
        {
            var confirmRemote = service.BindMode != HttpBindModes.All || MessageBox.Show(
                this,
                "MCP HTTP on 0.0.0.0 sends bearer tokens without encryption. Anyone who observes the network can reuse them. Continue?",
                "Enable remote MCP HTTP",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) == DialogResult.OK;
            if (!confirmRemote)
            {
                return;
            }

            await _client.EnableServiceAsync(confirmRemote, _lifetime.Token);
        }

        await RefreshCoreAsync();
    }

    private async Task AuthorizeClientAsync()
    {
        var name = TextPromptDialog.Ask(this, "Authorize MCP HTTP client", "Client name (1 to 64 characters)");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var result = await _client.AuthorizeClientAsync(name!, _lifetime.Token);
        TextPromptDialog.ShowSecret(this, result.Client.Name, result.Token);
        await RefreshCoreAsync();
    }

    private async Task DeauthorizeClientAsync()
    {
        var clientId = SelectedId(_clients);
        if (clientId is null)
        {
            MessageBox.Show(this, "Select an authorized client first.", "MCP HTTP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var message = clientId == "default"
            ? "Rotate the default CLI token and disconnect its active connections?"
            : "Deauthorize this client and disconnect its active connections? An operation already dispatched may have completed.";
        if (MessageBox.Show(this, message, "Deauthorize MCP HTTP client", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
            != DialogResult.OK)
        {
            return;
        }

        await _client.DeauthorizeClientAsync(clientId, _lifetime.Token);
        await RefreshCoreAsync();
    }

    private async Task DisconnectAsync()
    {
        var connectionId = SelectedId(_connections);
        if (connectionId is null)
        {
            MessageBox.Show(this, "Select an active connection first.", "MCP HTTP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                this,
                "Disconnect this MCP HTTP connection? An operation already dispatched may have completed.",
                "Disconnect MCP HTTP client",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        await _client.DisconnectAsync(connectionId, _lifetime.Token);
        await RefreshCoreAsync();
    }

    private async Task RotateDefaultTokenAsync()
    {
        if (MessageBox.Show(
                this,
                "Rotate the default CLI token and disconnect its active connections? An operation already dispatched may have completed.",
                "Rotate default CLI token",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        await _client.DeauthorizeClientAsync("default", _lifetime.Token);
        await RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        var serviceTask = _client.GetServiceStatusAsync(_lifetime.Token);
        var daemonTask = _client.GetDaemonStatusAsync(_lifetime.Token);
        var launchTask = Task.Run(_client.ReadLaunchRecord, _lifetime.Token);
        var clientsTask = _client.ListClientsAsync(_lifetime.Token);
        var connectionsTask = _client.ListConnectionsAsync(_lifetime.Token);
        await Task.WhenAll(serviceTask, clientsTask, connectionsTask, daemonTask, launchTask);
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        var service = serviceTask.Result;
        UpdateVersionText(daemonTask.Result, launchTask.Result);
        _lastService = service;
        _serviceState.Text = service.Running
            ? $"Listening on {service.BindAddress}:{service.Port}"
            : service.Enabled ? "Enabled, but not running" : "Disabled";
        // Older additive-protocol peers may omit the address hints. Never copy their wildcard bind URL.
        _endpoint.Text = $"http://127.0.0.1:{service.Port}/mcp";
        _lanEndpoints.UpdateEndpoints(service);
        _serviceError.Text = service.LastError ?? string.Empty;
        if (!_settingsDirty || service.Enabled)
        {
            _updatingSettings = true;
            try
            {
                _bindMode.SelectedItem = service.BindMode;
                _port.Value = service.Port;
                _settingsDirty = false;
            }
            finally
            {
                _updatingSettings = false;
            }
        }
        _toggle.Text = service.Enabled ? "&Disable" : "&Enable";
        _toggle.AccessibleName = service.Enabled ? "Disable MCP HTTP service" : "Enable MCP HTTP service";
        _bindMode.Enabled = !service.Enabled;
        _port.Enabled = !service.Enabled;
        _apply.Enabled = !service.Enabled;
        PopulateClients(clientsTask.Result);
        PopulateConnections(connectionsTask.Result);
    }

    private async Task RunOperationAsync(Func<Task> operation, bool showErrors = true)
    {
        if (_busy || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _busy = true;
        SetActionButtons(false);
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // A disposed pipe or control can finish with a non-cancellation error during unload.
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            _serviceError.Text = exception.Message;
            if (showErrors)
            {
                MessageBox.Show(this, exception.Message, "Fiddler Classic CLI", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _busy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                SetActionButtons(true);
            }
        }
    }

    private void PopulateClients(ListHttpClientsResponse response)
    {
        using var view = new GridRefreshState(_clients);
        _clients.Rows.Clear();
        foreach (var client in response.Clients)
        {
            var index = _clients.Rows.Add(
                client.ClientId,
                client.Name,
                client.CreatedAtUtc,
                client.LastSeenAtUtc ?? string.Empty,
                client.ActiveConnectionCount);
            _clients.Rows[index].Tag = client.ClientId;
        }
    }

    private void PopulateConnections(ListHttpConnectionsResponse response)
    {
        using var view = new GridRefreshState(_connections);
        _connections.Rows.Clear();
        foreach (var connection in response.Connections)
        {
            var index = _connections.Rows.Add(
                connection.ConnectionId,
                connection.RemoteEndpoint,
                connection.AuthorizationState,
                string.Join(", ", connection.ClientNames),
                connection.ConnectedAtUtc,
                connection.LastActivityAtUtc,
                connection.TotalRequestCount,
                connection.ActiveRequestCount);
            _connections.Rows[index].Tag = connection.ConnectionId;
        }
    }

    private void UpdateVersionText(DaemonStatus? daemon, HostLaunchRecord? launch)
    {
        var bridgeAssembly = typeof(BridgeControlPanel).Assembly;
        var protocolAssembly = typeof(DaemonProtocol).Assembly;
        _versions.Text = string.Join(Environment.NewLine, new[]
        {
            $"Fiddler Classic: {_fiddlerVersion}",
            $"Bridge: {AssemblyVersion(bridgeAssembly)}",
            $"Protocol: {AssemblyVersion(protocolAssembly)} (bridge protocol {ProtocolConstants.Version})",
            $"Configured host: {launch?.HostVersion ?? "not configured"}",
            $"Running daemon: {daemon?.HostVersion ?? "not running"}"
        });
    }

    private void SetActionButtons(bool enabled)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }
        _refresh.Enabled = enabled;
        var hasStatus = enabled && _lastService is not null;
        _toggle.Enabled = hasStatus && (_lastService!.Enabled || !_settingsDirty);
        _authorize.Enabled = hasStatus;
        _deauthorize.Enabled = hasStatus && SelectedId(_clients) is not null;
        _rotateDefault.Enabled = hasStatus;
        _disconnect.Enabled = hasStatus && SelectedId(_connections) is not null;
        // Copying known addresses is read-only and remains available during refreshes and mutations.
        _copyEndpoint.Enabled = !string.IsNullOrWhiteSpace(_endpoint.Text);
        _apply.Enabled = hasStatus && !_lastService!.Enabled;
    }


    private static string? SelectedId(DataGridView grid)
    {
        return grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as string;
    }

    private static string AssemblyVersion(Assembly assembly)
    {
        // Read version resources without instantiating unrelated Fiddler extension attributes.
        return FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}
