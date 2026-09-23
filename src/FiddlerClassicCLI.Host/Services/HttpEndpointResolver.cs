// Projects bounded local IPv4 client URLs without probing reachability or changing listener settings.
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal static class HttpEndpointResolver
{
    internal const int MaxLanEndpoints = 8;

    // Deferring address reads lets down and loopback adapters be skipped before querying their properties.
    internal sealed record Adapter(
        OperationalStatus Status,
        NetworkInterfaceType Type,
        Func<IEnumerable<IPAddress>> ReadAddresses);

    /// <summary>Adds address hints on the host worker, never on Fiddler's UI thread.</summary>
    /// <param name="status">The snapshot to enrich, leaving listener bind metadata unchanged.</param>
    /// <param name="readAdapters">Optional synthetic OS enumeration for independent tests.</param>
    /// <returns>The same snapshot, including loopback and at most eight distinct LAN URLs.</returns>
    internal static HttpServiceStatus Populate(HttpServiceStatus status, Func<IEnumerable<Adapter>>? readAdapters = null)
    {
        ConfigStore.ValidatePort(status.Port);
        var port = status.Port.ToString(CultureInfo.InvariantCulture);
        status.LoopbackEndpoint = $"http://127.0.0.1:{port}/mcp";
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        if (status.BindMode == HttpBindModes.All)
        {
            try
            {
                foreach (var adapter in (readAdapters ?? ReadAdapters)().Take(128))
                {
                    if (adapter.Status != OperationalStatus.Up || adapter.Type == NetworkInterfaceType.Loopback)
                        continue;

                    try
                    {
                        foreach (var address in adapter.ReadAddresses().Take(128))
                        {
                            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                                continue;
                            var bytes = address.GetAddressBytes();
                            if (bytes[0] == 0 || bytes[0] >= 224)
                                continue;

                            endpoints.Add($"http://{address}:{port}/mcp");
                            if (endpoints.Count == MaxLanEndpoints)
                                break;
                        }
                    }
                    catch (NetworkInformationException)
                    {
                        // An adapter may disappear during enumeration. Keep hints from other adapters.
                    }

                    if (endpoints.Count == MaxLanEndpoints)
                        break;
                }
            }
            catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
            {
                // Discovery is best effort. Status and the loopback URL remain available.
            }
        }

        status.LanEndpoints = endpoints.OrderBy(endpoint => endpoint, StringComparer.Ordinal).ToArray();
        return status;
    }

    private static IEnumerable<Adapter> ReadAdapters() => NetworkInterface.GetAllNetworkInterfaces().Select(adapter =>
        new Adapter(adapter.OperationalStatus, adapter.NetworkInterfaceType,
            () => adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address)));
}
