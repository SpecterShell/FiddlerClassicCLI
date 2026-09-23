// Applies staged binding edits through the existing confirmed stop/configure/start controls.
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel
{
    /// <summary>Restarts an enabled listener only after validating edits and confirming their effects.</summary>
    /// <param name="request">Binding and port values captured when Apply was clicked.</param>
    /// <returns>False when a warning is cancelled, otherwise true after successful application.</returns>
    private async Task<bool> ApplyBindingsAsync(ConfigureHttpServiceRequest request)
    {
        if (request.BindMode == HttpBindModes.Selected && request.BindAddresses!.Length == 0)
            throw new InvalidOperationException("Select at least one IPv4 address before applying listener settings.");

        var current = await _client.GetServiceStatusAsync(_lifetime.Token);
        _lastService = current;
        if (current.BindMode == request.BindMode && current.Port == request.Port
            && (request.BindMode != HttpBindModes.Selected
                || new HashSet<string>(current.BindAddresses, StringComparer.Ordinal).SetEquals(request.BindAddresses!)))
            return true;

        var resume = IsServiceActive(current);
        var confirmedAccess = false;
        if (resume)
        {
            if (!ConfirmAccess(current.AuthenticationMode, IsRemote(request.BindMode!, request.BindAddresses!),
                    "Apply MCP HTTP settings", out confirmedAccess)) return false;
            // The same risk acknowledgment also covers saved automatic-start settings.
            request.Confirm = confirmedAccess;
            if (current.ActiveConnectionCount > 0 && !_confirm(
                    "Applying these settings will restart MCP HTTP and disconnect active clients. " +
                    "An operation already dispatched may have completed. Continue?", "Apply MCP HTTP settings")) return false;
        }
        else if (!ConfirmConfiguration(request)) return false;

        try
        {
            if (resume)
                _lastService = await _client.DisableServiceAsync(current.ActiveConnectionCount > 0, _lifetime.Token);
            _lastService = await _client.ConfigureServiceAsync(request, _lifetime.Token);
            if (resume)
                _lastService = await _client.EnableServiceAsync(confirmedAccess, _lifetime.Token);
            return true;
        }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            // Reconcile a partially completed restart. Keep the edit and original error visible,
            // and never automatically retry an uncertain mutation or widen a failed binding.
            try { await RefreshCoreAsync(); }
            catch (Exception) when (!_lifetime.IsCancellationRequested) { }
            throw;
        }
    }
}
