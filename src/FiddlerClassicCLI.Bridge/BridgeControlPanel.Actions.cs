// Handles explicit management actions with confirmation and captured user intent.
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel
{
    /// <summary>Captures action inputs on the UI thread before asynchronous refresh coordination.</summary>
    private void WireEvents()
    {
        _bindMode.SelectedIndexChanged += (_, _) => SettingsChanged();
        _port.ValueChanged += (_, _) => SettingsChanged();
        _port.TextChanged += (_, _) => SettingsChanged();
        _interfaces.SelectionEdited += (_, _) => SettingsChanged();
        _startup.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingSettings) _startupDirty = true;
            SetActionButtons(!_busy);
        };
        _authenticationMode.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingSettings) _authenticationDirty = true;
            SetActionButtons(!_busy);
        };
        _clients.SelectionChanged += (_, _) => SetActionButtons(!_busy);
        _connections.SelectionChanged += (_, _) => SetActionButtons(!_busy);
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(RefreshCoreAsync);
        _refresh.Click += async (_, _) => await RefreshAsync(RefreshCoreAsync, showErrors: true);
        _apply.Click += async (_, _) =>
        {
            // Capture intent before waiting for a background read to finish.
            var bindMode = _bindMode.SelectedItem?.ToString() ?? HttpBindModes.Loopback;
            var port = decimal.ToInt32(_port.Value);
            var addresses = bindMode == HttpBindModes.Selected ? _interfaces.SelectedAddresses : Array.Empty<string>();
            await RunOperationAsync(async () =>
            {
                var request = new ConfigureHttpServiceRequest { BindMode = bindMode, Port = port, BindAddresses = addresses };
                if (!await ApplyBindingsAsync(request)) return;
                // Preserve any newer edits made while this request was in flight.
                if (_bindMode.SelectedItem?.ToString() == bindMode && _port.Value == port
                    && (bindMode != HttpBindModes.Selected || _interfaces.SelectedAddresses.SequenceEqual(addresses)))
                    _settingsDirty = false;
                await RefreshCoreAsync();
            });
        };
        _applyPreferences.Click += async (_, _) =>
        {
            var request = new ConfigureHttpServiceRequest
            {
                StartupMode = _startupDirty ? _startup.SelectedItem?.ToString() : null,
                AuthenticationMode = _authenticationDirty && !IsServiceActive(_lastService)
                    ? SelectedAuthenticationMode : null
            };
            await RunOperationAsync(async () =>
            {
                if (!ConfirmConfiguration(request)) return;
                _lastService = await _client.ConfigureServiceAsync(request, _lifetime.Token);
                if (request.StartupMode == _startup.SelectedItem?.ToString()) _startupDirty = false;
                if (request.AuthenticationMode is not null && request.AuthenticationMode == SelectedAuthenticationMode) _authenticationDirty = false;
                await RefreshCoreAsync();
            });
        };
        _enabled.CheckedChanged += async (_, _) =>
        {
            if (_updatingSettings || _pendingEnabledChange) return;
            var enable = _enabled.Checked;
            _pendingEnabledChange = true;
            try { await RunOperationAsync(() => SetServiceEnabledAsync(enable)); }
            finally
            {
                _pendingEnabledChange = false;
                if (!_lifetime.IsCancellationRequested) UpdateEnabledCheck();
            }
        };
        _copyEndpoint.GetCopyText = () => _endpoint.Text;
        _authorize.Click += async (_, _) => await RunOperationAsync(AuthorizeClientAsync);
        _deauthorize.Click += async (_, _) =>
        {
            var clientId = SelectedId(_clients);
            await RunOperationAsync(() => DeauthorizeClientAsync(clientId));
        };
        _rotateDefault.Click += async (_, _) => await RunOperationAsync(RotateDefaultTokenAsync);
        _disconnect.Click += async (_, _) =>
        {
            var connectionId = SelectedId(_connections);
            await RunOperationAsync(() => DisconnectAsync(connectionId));
        };
    }

    private void SettingsChanged()
    {
        if (!_updatingSettings)
        {
            _settingsDirty = true;
            SetActionButtons(!_busy);
        }
        _interfaces.Visible = _bindMode.SelectedItem?.ToString() == HttpBindModes.Selected;
    }

    /// <summary>Applies the requested state with the existing plaintext and disconnect confirmations.</summary>
    /// <param name="enable">The direction captured from the button at click time.</param>
    private async Task SetServiceEnabledAsync(bool enable)
    {
        var service = _lastService ?? await _client.GetServiceStatusAsync(_lifetime.Token);
        if (!enable)
        {
            var confirm = service.ActiveConnectionCount == 0 || _confirm(
                "Disabling MCP HTTP will disconnect active clients. An operation already dispatched may have completed.",
                "Disable MCP HTTP");
            if (!confirm)
            {
                return;
            }

            _lastService = await _client.DisableServiceAsync(confirm: service.ActiveConnectionCount > 0, _lifetime.Token);
        }
        else
        {
            if (!ConfirmAccess(service.AuthenticationMode, IsRemote(service.BindMode, service.BindAddresses), "Enable MCP HTTP", out var confirmed)) return;
            _lastService = await _client.EnableServiceAsync(confirmed, _lifetime.Token);
        }

        await RefreshCoreAsync();
    }

    private async Task AuthorizeClientAsync()
    {
        if (_lastService is null || _lastService.AuthenticationMode == HttpAuthenticationModes.None) return;
        var name = TextPromptDialog.Ask(this, "Authorize MCP HTTP client", "Client name (1 to 64 characters)");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var result = await _client.AuthorizeClientAsync(name!, _lifetime.Token);
        TextPromptDialog.ShowSecret(this, result.Client.Name, result.Token);
        await RefreshCoreAsync();
    }

    private async Task DeauthorizeClientAsync(string? clientId)
    {
        if (_lastService is null || _lastService.AuthenticationMode == HttpAuthenticationModes.None) return;
        if (clientId is null)
        {
            MessageBox.Show(this, "Select an authorized client first.", "MCP HTTP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var message = clientId == "default"
            ? "Rotate the default CLI token and disconnect its active connections?"
            : $"Deauthorize client '{clientId}' and disconnect its active connections? An operation already dispatched may have completed.";
        if (MessageBox.Show(this, message, "Deauthorize MCP HTTP client", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
            != DialogResult.OK)
        {
            return;
        }

        await _client.DeauthorizeClientAsync(clientId, _lifetime.Token);
        await RefreshCoreAsync();
    }

    private async Task DisconnectAsync(string? connectionId)
    {
        if (connectionId is null)
        {
            MessageBox.Show(this, "Select an active connection first.", "MCP HTTP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                this,
                $"Disconnect MCP HTTP connection '{connectionId}'? An operation already dispatched may have completed.",
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
        if (_lastService is null || _lastService.AuthenticationMode == HttpAuthenticationModes.None) return;
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
}
