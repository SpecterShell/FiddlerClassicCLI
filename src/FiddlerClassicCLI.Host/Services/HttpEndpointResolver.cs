// Projects explicit listener URLs and bounded local IPv4 choices without probing reachability.
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal static class HttpEndpointResolver
{
    internal const int MaxLanEndpoints = 8;

    // Defer address reads so unavailable adapters can be skipped without querying their properties.
    internal sealed record Adapter(OperationalStatus Status, NetworkInterfaceType Type,
        Func<IEnumerable<IPAddress>> ReadAddresses, string Name = "Network adapter");

    /// <summary>Builds a credential-free snapshot shared by online and offline administration.</summary>
    /// <param name="configuration">The validated current-user settings.</param>
    /// <param name="running">Whether the complete configured listener set is active.</param>
    /// <param name="connections">Current transport connection count.</param>
    /// <param name="lastError">An operational error without traffic or credentials.</param>
    internal static HttpServiceStatus FromConfiguration(HostConfiguration configuration,
        bool running = false, int connections = 0, string? lastError = null)
    {
        return Populate(new HttpServiceStatus
        {
            Enabled = configuration.HttpServiceEnabled,
            Running = running,
            BindMode = configuration.HttpBindMode,
            BindAddresses = HttpListenerSettings.GetAddresses(configuration).Select(address => address.ToString()).ToArray(),
            Port = configuration.HttpPort,
            StartupMode = configuration.HttpStartupMode,
            AuthenticationMode = configuration.HttpAuthenticationMode,
            ActiveConnectionCount = connections,
            LastError = lastError
        });
    }

    /// <summary>Adds local interface choices and client URLs on the host worker, never on the UI thread.</summary>
    /// <param name="status">The listener snapshot to enrich.</param>
    /// <param name="readAdapters">Optional OS adapter reader for deterministic tests.</param>
    /// <returns>The snapshot with every explicit endpoint and bounded all-interface URL hints.</returns>
    internal static HttpServiceStatus Populate(HttpServiceStatus status, Func<IEnumerable<Adapter>>? readAdapters = null)
    {
        ConfigStore.ValidatePort(status.Port);
        var port = status.Port.ToString(CultureInfo.InvariantCulture);
        status.AvailableInterfaces = ReadInterfaceAddresses(readAdapters);
        status.BindAddresses = status.BindMode switch
        {
            HttpBindModes.All => ["0.0.0.0"],
            HttpBindModes.Loopback => ["127.0.0.1"],
            _ => status.BindAddresses.ToArray()
        };
        status.Endpoints = status.BindAddresses.Select(address => $"http://{address}:{port}/mcp").ToArray();
        status.BindAddress = status.BindAddresses.FirstOrDefault() ?? string.Empty;
        status.Endpoint = status.Endpoints.FirstOrDefault() ?? string.Empty;
        var loopback = status.BindMode == HttpBindModes.All ? "127.0.0.1"
            : status.BindAddresses.FirstOrDefault(address => IPAddress.IsLoopback(IPAddress.Parse(address)));
        status.LoopbackEndpoint = loopback is null ? string.Empty : $"http://{loopback}:{port}/mcp";
        var lan = status.BindMode == HttpBindModes.All
            ? status.AvailableInterfaces.Select(item => item.Address).Where(address => !IPAddress.IsLoopback(IPAddress.Parse(address))).Take(MaxLanEndpoints)
            : status.BindAddresses.Where(address => !IPAddress.IsLoopback(IPAddress.Parse(address)));
        status.LanEndpoints = lan.Select(address => $"http://{address}:{port}/mcp").OrderBy(url => url, StringComparer.Ordinal).ToArray();
        return status;
    }

    /// <summary>Enumerates active unicast IPv4 addresses, keeping loopback available during adapter failures.</summary>
    /// <param name="readAdapters">Optional synthetic adapter enumeration.</param>
    internal static HttpInterfaceAddressDto[] ReadInterfaceAddresses(Func<IEnumerable<Adapter>>? readAdapters = null)
    {
        var choices = new Dictionary<string, HttpInterfaceAddressDto>(StringComparer.Ordinal)
        {
            ["127.0.0.1"] = new() { Address = "127.0.0.1", AdapterName = "Loopback" }
        };
        try
        {
            foreach (var adapter in (readAdapters ?? ReadAdapters)().Take(128))
            {
                if (adapter.Status != OperationalStatus.Up || adapter.Type == NetworkInterfaceType.Loopback) continue;
                try
                {
                    foreach (var address in adapter.ReadAddresses().Take(128))
                    {
                        var text = address.ToString();
                        if (!HttpListenerSettings.IsUnicastAddress(text) || IPAddress.IsLoopback(address)) continue;
                        choices.TryAdd(text, new HttpInterfaceAddressDto
                        {
                            Address = text,
                            AdapterName = adapter.Name.Length <= 128 ? adapter.Name : adapter.Name[..128]
                        });
                        if (choices.Count >= HttpListenerLimits.MaximumAvailableInterfaces) break;
                    }
                }
                catch (NetworkInformationException) { }
                if (choices.Count >= HttpListenerLimits.MaximumAvailableInterfaces) break;
            }
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException) { }
        return choices.Values.OrderBy(item => item.Address, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<Adapter> ReadAdapters() => NetworkInterface.GetAllNetworkInterfaces().Select(adapter =>
        new Adapter(adapter.OperationalStatus, adapter.NetworkInterfaceType,
            () => adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address), adapter.Name));
}
