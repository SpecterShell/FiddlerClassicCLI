// Applies HTTP listener preferences atomically, including security consent and startup policy.
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal sealed partial class ConfigStore
{
    /// <summary>Updates HTTP settings in one transaction, retaining unrelated credentials and state.</summary>
    /// <param name="request">Only supplied settings change. Confirmation acknowledges exposure risks.</param>
    public HostConfiguration ConfigureHttpService(ConfigureHttpServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using (ConfigFileLock.Acquire(ConfigPath))
        {
            var configuration = GetOrCreateUnsafe();
            var listenerSettings = request.BindMode is not null || request.BindAddresses is not null
                || request.Port.HasValue || request.AuthenticationMode is not null;
            if (!listenerSettings && request.StartupMode is null)
                throw new HttpAdministrationException(ErrorCodes.InvalidRequest, "Specify at least one HTTP service setting.");
            if (listenerSettings && configuration.HttpServiceEnabled)
                throw new HttpAdministrationException(ErrorCodes.Conflict,
                    "Disable the managed MCP HTTP service before changing its bindings, port, or authentication.");

            if (request.BindMode is not null) configuration.HttpBindMode = request.BindMode;
            if (request.BindAddresses is not null) configuration.HttpBindAddresses = request.BindAddresses.ToArray();
            else if (request.BindMode is not null && request.BindMode != HttpBindModes.Selected)
                configuration.HttpBindAddresses = Array.Empty<string>();
            if (request.Port.HasValue) configuration.HttpPort = request.Port.Value;
            if (request.StartupMode is not null) configuration.HttpStartupMode = request.StartupMode;
            if (request.AuthenticationMode is not null) configuration.HttpAuthenticationMode = request.AuthenticationMode;
            ValidatePort(configuration.HttpPort);
            HttpListenerSettings.Validate(configuration);

            if (!request.Confirm && (request.AuthenticationMode == HttpAuthenticationModes.None
                || (configuration.HttpStartupMode == HttpStartupModes.Enabled
                    && HttpListenerSettings.RequiresEnableConfirmation(configuration))))
                throw new HttpAdministrationException(ErrorCodes.ConfirmationRequired,
                    HttpListenerSettings.ExposureWarning(configuration));
            return SaveUnsafe(configuration);
        }
    }

    /// <summary>Checks exposure consent against the same configuration snapshot that is enabled.</summary>
    /// <param name="enabled">The desired persistent service state.</param>
    /// <param name="confirm">Explicit consent for remote or unauthenticated enablement.</param>
    public HostConfiguration SetHttpServiceEnabled(bool enabled, bool confirm = false)
    {
        using (ConfigFileLock.Acquire(ConfigPath))
        {
            var configuration = GetOrCreateUnsafe();
            if (enabled && !confirm && HttpListenerSettings.RequiresEnableConfirmation(configuration))
                throw new HttpAdministrationException(ErrorCodes.ConfirmationRequired,
                    HttpListenerSettings.ExposureWarning(configuration));
            configuration.HttpServiceEnabled = enabled;
            return SaveUnsafe(configuration);
        }
    }

    /// <summary>Applies the saved startup preference without altering the preference itself.</summary>
    /// <returns>The desired listener configuration for this startup.</returns>
    internal HostConfiguration ApplyHttpStartup()
    {
        using (ConfigFileLock.Acquire(ConfigPath))
        {
            var configuration = GetOrCreateUnsafe();
            var enabled = configuration.HttpStartupMode switch
            {
                HttpStartupModes.Enabled => true,
                HttpStartupModes.Disabled => false,
                _ => configuration.HttpServiceEnabled
            };
            if (configuration.HttpServiceEnabled == enabled) return configuration;
            configuration.HttpServiceEnabled = enabled;
            return SaveUnsafe(configuration);
        }
    }
}
