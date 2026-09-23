// Serializes background snapshots and user operations while preserving the current view.
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel
{
    /// <summary>Coalesces read-only refreshes and skips polling while an explicit action owns the panel.</summary>
    /// <param name="read">The full or pipe-only snapshot operation, executed on the UI context.</param>
    /// <param name="showErrors">Whether a manually requested refresh may display a failure dialog.</param>
    /// <returns>The existing refresh or a new non-overlapping refresh task.</returns>
    private Task RefreshAsync(Func<Task> read, bool showErrors = false)
    {
        if (_busy || _lifetime.IsCancellationRequested) return Task.CompletedTask;
        if (!_refreshTask.IsCompleted) return _refreshTask;
        return _refreshTask = RunRefreshAsync(read, showErrors);
    }

    private async Task RunRefreshAsync(Func<Task> read, bool showErrors)
    {
        _refreshing = true;
        SetActionButtons(!_busy);
        try { await ReportFailuresAsync(read, showErrors); }
        finally
        {
            _refreshing = false;
            if (!_lifetime.IsCancellationRequested) SetActionButtons(!_busy);
        }
    }

    /// <summary>Reads independent status sources concurrently and applies their metadata on the UI context.</summary>
    /// <returns>Completion of the snapshot, including version and pipe diagnostics on HTTP failure.</returns>
    private async Task RefreshCoreAsync()
    {
        var serviceTask = _client.GetServiceStatusAsync(_lifetime.Token);
        var daemonTask = RefreshPipeStatusAsync();
        var launchTask = Task.Run(_client.ReadLaunchRecord, _lifetime.Token);
        var clientsTask = _client.ListClientsAsync(_lifetime.Token);
        var connectionsTask = _client.ListConnectionsAsync(_lifetime.Token);
        try
        {
            await Task.WhenAll(serviceTask, clientsTask, connectionsTask, daemonTask, launchTask);
        }
        catch
        {
            // Never keep mutation controls enabled from a stale snapshot after a peer mismatch or failed read.
            _lastService = null;
            throw;
        }
        finally
        {
            // HTTP failures must not leave a stale running-daemon version after a pipe check fails.
            if (!_lifetime.IsCancellationRequested)
            {
                UpdateVersionText(
                    daemonTask.Status == TaskStatus.RanToCompletion ? daemonTask.Result : null,
                    launchTask.Status == TaskStatus.RanToCompletion ? launchTask.Result : null);
            }
        }
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        var service = serviceTask.Result;
        _lastService = service;
        _enabled.AccessibleDescription = service.Running
            ? "Listening on " + string.Join(", ", (service.BindAddresses.Length > 1 ? service.BindAddresses : new[] { service.BindAddress })
                .Select(address => $"{address}:{service.Port}"))
            : service.Enabled ? "Enabled, stopped" : "Disabled";
        // Older additive-protocol peers may omit the address hints. Never copy their wildcard bind URL.
        _endpoint.Text = service.BindMode == HttpBindModes.Selected ? service.LoopbackEndpoint : $"http://127.0.0.1:{service.Port}/mcp";
        _copyEndpoint.AccessibleDescription = _endpoint.Text;
        _lanEndpoints.UpdateEndpoints(service);
        _serviceError.Text = service.LastError ?? string.Empty;
        _authenticationWarning.ForeColor = service.AuthenticationMode == HttpAuthenticationModes.None ? Color.DarkRed : SystemColors.ControlText;
        _authenticationWarning.Text = service.AuthenticationMode switch
        {
            HttpAuthenticationModes.None => "Authentication is disabled. Anyone who can reach the listener has full MCP access.",
            HttpAuthenticationModes.NonLoopback => "Connections using loopback addresses at both ends have full MCP access without authentication. Other connections require a bearer token.",
            _ => string.Empty
        };
        _updatingSettings = true;
        try
        {
            if (!_settingsDirty)
            {
                _bindMode.SelectedItem = service.BindMode;
                _port.Value = service.Port;
            }
            _interfaces.UpdateAddresses(service.AvailableInterfaces, _settingsDirty ? _interfaces.SelectedAddresses : service.BindAddresses);
            if (!_startupDirty) _startup.SelectedItem = service.StartupMode;
            if (!_authenticationDirty) _authenticationMode.SelectedItem = _authenticationMode.Items.Cast<AuthenticationChoice>()
                .FirstOrDefault(choice => choice.Mode == service.AuthenticationMode);
        }
        finally { _updatingSettings = false; }
        if (!_pendingEnabledChange) UpdateEnabledCheck();
        PopulateClients(clientsTask.Result);
        PopulateConnections(connectionsTask.Result);
    }

    /// <summary>Queues one explicit action after the current read. Further clicks and polls are skipped.</summary>
    /// <param name="operation">An action whose inputs were captured at click time.</param>
    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (_busy || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _busy = true;
        SetActionButtons(false);
        try
        {
            await _refreshTask;
            if (!_lifetime.IsCancellationRequested) await ReportFailuresAsync(operation, showErrors: true);
        }
        finally
        {
            _busy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                UpdateBridgePipeStatus();
                SetActionButtons(true);
            }
        }
    }

    private async Task ReportFailuresAsync(Func<Task> operation, bool showErrors)
    {
        try { await operation(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // A disposed pipe or control can finish with a non-cancellation error during unload.
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            _enabled.AccessibleDescription = "Status unavailable";
            _serviceError.Text = exception.Message;
            if (showErrors)
            {
                _showError(exception.Message);
            }
        }
    }

    private void PopulateClients(ListHttpClientsResponse response)
    {
        _clients.UpdateRows(response.Clients.Select(client => new object[]
        {
            client.ClientId,
            client.Name,
            client.CreatedAtUtc,
            client.LastSeenAtUtc ?? string.Empty,
            client.ActiveConnectionCount
        }));
    }

    private void PopulateConnections(ListHttpConnectionsResponse response)
    {
        _connections.UpdateRows(response.Connections.Select(connection => new object[]
        {
            connection.ConnectionId,
            connection.RemoteEndpoint,
            connection.AuthorizationState,
            string.Join(", ", connection.ClientNames),
            connection.ConnectedAtUtc,
            connection.LastActivityAtUtc,
            connection.TotalRequestCount,
            connection.ActiveRequestCount
        }));
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
            $"Running daemon: {daemon?.HostVersion ?? "unavailable"}"
        });
    }

    private static string AssemblyVersion(Assembly assembly)
    {
        // Read version resources without instantiating unrelated Fiddler extension attributes.
        return FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}
