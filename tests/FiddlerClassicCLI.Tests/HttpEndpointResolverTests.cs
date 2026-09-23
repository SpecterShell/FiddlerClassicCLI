// Checks address hints using synthetic adapters without a daemon, listener, configuration, or live network.
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class HttpEndpointResolverTests
{
    [Theory]
    [InlineData(HttpBindModes.Loopback, "127.0.0.1")]
    [InlineData(HttpBindModes.All, "0.0.0.0")]
    public void AddsClientUrlsWithoutChangingListenerMetadata(string mode, string address)
    {
        var original = new HttpServiceStatus
        {
            BindMode = mode,
            BindAddress = address,
            Port = 9101,
            Endpoint = $"http://{address}:9101/mcp",
            Enabled = true,
            Running = true
        };
        var readCount = 0;
        var status = HttpEndpointResolver.Populate(original, () =>
        {
            readCount++;
            return new[] { Adapter("192.168.10.4", "10.0.0.2", "192.168.10.4") };
        });

        Assert.Same(original, status);
        Assert.Equal(address, status.BindAddress);
        Assert.Equal($"http://{address}:9101/mcp", status.Endpoint);
        Assert.Equal("http://127.0.0.1:9101/mcp", status.LoopbackEndpoint);
        Assert.True(status.Enabled);
        Assert.True(status.Running);
        Assert.Equal(1, readCount);
        Assert.Equal(3, status.AvailableInterfaces.Length);
        Assert.Equal(mode == HttpBindModes.All
            ? new[] { "http://10.0.0.2:9101/mcp", "http://192.168.10.4:9101/mcp" }
            : Array.Empty<string>(), status.LanEndpoints);
    }

    [Fact]
    public void SkipsDownAndLoopbackAdaptersBeforeReadingAddressesAndRejectsNonLanAddresses()
    {
        var status = HttpEndpointResolver.Populate(new HttpServiceStatus { BindMode = HttpBindModes.All }, () =>
        [
            new(OperationalStatus.Down, NetworkInterfaceType.Ethernet, () => throw new InvalidOperationException("Down adapter was read.")),
            new(OperationalStatus.Up, NetworkInterfaceType.Loopback, () => throw new InvalidOperationException("Loopback adapter was read.")),
            Adapter("127.0.0.1", "127.2.3.4", "::1", "fe80::1", "2001:db8::1", "0.0.0.0", "224.0.0.1", "255.255.255.255", "192.168.1.9")
        ]);
        Assert.Equal(new[] { "http://192.168.1.9:8877/mcp" }, status.LanEndpoints);
    }

    [Fact]
    public void BoundsAndDeduplicatesAddressHints()
    {
        var status = HttpEndpointResolver.Populate(new HttpServiceStatus { BindMode = HttpBindModes.All }, () =>
        [
            Adapter(Enumerable.Range(1, 30).SelectMany(index => new[] { $"10.0.0.{index}", $"10.0.0.{index}" }).ToArray())
        ]);
        Assert.Equal(HttpEndpointResolver.MaxLanEndpoints, status.LanEndpoints.Length);
        Assert.Equal(status.LanEndpoints.Length, status.LanEndpoints.Distinct().Count());
    }

    [Fact]
    public void AdapterFailureDoesNotHideOtherAddressesOrLoopback()
    {
        var status = HttpEndpointResolver.Populate(new HttpServiceStatus { BindMode = HttpBindModes.All }, () =>
        [
            new(OperationalStatus.Up, NetworkInterfaceType.Ethernet, () => throw new NetworkInformationException()),
            Adapter("10.0.0.4")
        ]);
        Assert.Equal(new[] { "http://10.0.0.4:8877/mcp" }, status.LanEndpoints);
        HttpEndpointResolver.Populate(status, () => throw new NetworkInformationException());
        Assert.Empty(status.LanEndpoints);
        Assert.Equal("http://127.0.0.1:8877/mcp", status.LoopbackEndpoint);
    }

    [Fact]
    public void AdditiveFieldsRoundTripAndOlderSnapshotsRemainReadable()
    {
        var status = HttpEndpointResolver.Populate(new HttpServiceStatus { BindMode = HttpBindModes.All }, () => [Adapter("10.0.0.4")]);
        var roundTrip = JsonSerializer.Deserialize<HttpServiceStatus>(JsonSerializer.Serialize(status))!;
        Assert.Equal(status.LoopbackEndpoint, roundTrip.LoopbackEndpoint);
        Assert.Equal(status.LanEndpoints, roundTrip.LanEndpoints);
        var old = JsonSerializer.Deserialize<HttpServiceStatus>("{\"Port\":9000,\"BindMode\":\"all\"}")!;
        Assert.Empty(old.LanEndpoints);
        Assert.Empty(old.LoopbackEndpoint);
        Assert.Equal(1, DaemonProtocol.Version);
    }

    [Fact]
    public void SelectedBindingsAdvertiseOnlyTheirActualEndpoints()
    {
        var status = HttpEndpointResolver.Populate(new HttpServiceStatus
        {
            BindMode = HttpBindModes.Selected, BindAddresses = ["10.0.0.4", "192.168.1.2"], Port = 9001
        }, () => [Adapter("10.0.0.4", "192.168.1.2", "10.0.0.9")]);
        Assert.Empty(status.LoopbackEndpoint);
        Assert.Equal(new[] { "http://10.0.0.4:9001/mcp", "http://192.168.1.2:9001/mcp" }, status.Endpoints);
        Assert.Equal(status.Endpoints, status.LanEndpoints);
        Assert.Equal(status.Endpoints[0], status.Endpoint);
        Assert.Contains(status.AvailableInterfaces, item => item.Address == "10.0.0.9");
        status.BindAddresses = ["127.0.0.1", "10.0.0.4"];
        HttpEndpointResolver.Populate(status, () => []);
        Assert.Equal("http://127.0.0.1:9001/mcp", status.LoopbackEndpoint);
        Assert.Equal(new[] { "http://10.0.0.4:9001/mcp" }, status.LanEndpoints);
    }

    [Fact]
    public void InterfaceChoicesAreNamedDeduplicatedAndBoundedEvenInLoopbackMode()
    {
        var status = HttpEndpointResolver.Populate(new HttpServiceStatus(), () =>
        [
            new(OperationalStatus.Up, NetworkInterfaceType.Ethernet,
                () => Enumerable.Range(1, 100).Select(index => IPAddress.Parse($"10.0.0.{index}")), "Test adapter")
        ]);
        Assert.Equal(HttpListenerLimits.MaximumAvailableInterfaces, status.AvailableInterfaces.Length);
        Assert.Contains(status.AvailableInterfaces, item => item.Address == "127.0.0.1" && item.AdapterName == "Loopback");
        Assert.All(status.AvailableInterfaces.Where(item => item.Address != "127.0.0.1"), item => Assert.Equal("Test adapter", item.AdapterName));
        Assert.Empty(status.LanEndpoints);
        Assert.Equal(HttpListenerLimits.MaximumAvailableInterfaces, status.AvailableInterfaces.Select(item => item.Address).Distinct().Count());
    }

    private static HttpEndpointResolver.Adapter Adapter(params string[] addresses) =>
        new(OperationalStatus.Up, NetworkInterfaceType.Ethernet, () => addresses.Select(IPAddress.Parse));
}
