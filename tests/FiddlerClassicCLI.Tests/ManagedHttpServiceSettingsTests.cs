// Exercises startup policy and explicit-interface failure behavior with isolated real listeners.
using System.Net;
using System.Net.Sockets;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed partial class ManagedHttpServiceTests
{
    [Fact]
    public async Task DefaultManagedListenerAllowsLocalRequestsWithoutCredentials()
    {
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { Port = GetFreePort() });
        await using var manager = new HttpServiceManager(store);
        var status = await manager.EnableAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(HttpAuthenticationModes.NonLoopback, status.AuthenticationMode);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(status.Endpoint) };
        using var request = HttpPolicyTestServer.Request();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = Assert.Single(manager.ListConnections().Connections);
        Assert.Equal("anonymous", record.AuthorizationState);
        Assert.Empty(record.ClientIds);
        Assert.Empty(record.ClientNames);
        var error = await Assert.ThrowsAsync<HttpAdministrationException>(() => manager.ConfigureAsync(
            new ConfigureHttpServiceRequest { AuthenticationMode = HttpAuthenticationModes.Required },
            TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.Conflict, error.Code);
        await manager.DisableAsync(true, TestContext.Current.CancellationToken);
        var stopped = await manager.ConfigureAsync(new ConfigureHttpServiceRequest
            { AuthenticationMode = HttpAuthenticationModes.Required }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpAuthenticationModes.Required, stopped.AuthenticationMode);
    }

    [Theory]
    [InlineData(HttpStartupModes.Enabled, false, true)]
    [InlineData(HttpStartupModes.Disabled, true, false)]
    [InlineData(HttpStartupModes.LastState, true, true)]
    [InlineData(HttpStartupModes.LastState, false, false)]
    public async Task AppliesConfiguredStartupStateToRealListener(string mode, bool previous, bool expected)
    {
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { Port = GetFreePort(), StartupMode = mode });
        store.SetHttpServiceEnabled(previous);
        await using var manager = new HttpServiceManager(store);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, manager.GetStatus().Running);
        Assert.Equal(expected, manager.GetStatus().Enabled);
        Assert.Equal(mode, manager.GetStatus().StartupMode);
    }

    [Fact]
    public async Task AppliesPreferenceAgainOnFiddlerStartupWithoutRestartingDaemon()
    {
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { Port = GetFreePort(), StartupMode = HttpStartupModes.Disabled });
        await using var manager = new HttpServiceManager(store);
        await manager.EnableAsync(false, TestContext.Current.CancellationToken);
        var saved = await manager.ConfigureAsync(new ConfigureHttpServiceRequest { StartupMode = HttpStartupModes.Disabled }, TestContext.Current.CancellationToken);
        Assert.True(saved.Running);
        var stopped = await manager.ApplyStartupAsync(TestContext.Current.CancellationToken);
        Assert.False(stopped.Running);
        Assert.False(stopped.Enabled);
        await manager.ConfigureAsync(new ConfigureHttpServiceRequest { StartupMode = HttpStartupModes.Enabled }, TestContext.Current.CancellationToken);
        Assert.True((await manager.ApplyStartupAsync(TestContext.Current.CancellationToken)).Running);
    }

    [Fact]
    public async Task MissingSelectedInterfaceLeavesNoFallbackListenerAndCanRecover()
    {
        var port = GetFreePort();
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest
        {
            Port = port, BindMode = HttpBindModes.Selected, BindAddresses = ["127.0.0.1", "192.0.2.10"]
        });
        await using var manager = new HttpServiceManager(store, () => [new() { Address = "127.0.0.1", AdapterName = "Test loopback" }]);
        var error = await Assert.ThrowsAsync<HttpAdministrationException>(() => manager.EnableAsync(true, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.Unavailable, error.Code);
        var status = manager.GetStatus();
        Assert.False(status.Running);
        Assert.True(status.Enabled);
        Assert.Contains("192.0.2.10", status.LastError);
        using (var probe = new TcpListener(IPAddress.Loopback, port)) probe.Start();
        await manager.DisableAsync(true, TestContext.Current.CancellationToken);
        await manager.ConfigureAsync(new ConfigureHttpServiceRequest { BindAddresses = ["127.0.0.1"] }, TestContext.Current.CancellationToken);
        Assert.True((await manager.EnableAsync(false, TestContext.Current.CancellationToken)).Running);
    }

    [Fact]
    public async Task RunningStatusRetainsActualAccessPolicyAfterExternalConfigurationEdit()
    {
        var token = TestContext.Current.CancellationToken;
        var port = GetFreePort();
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest
        {
            Port = port, AuthenticationMode = HttpAuthenticationModes.None, Confirm = true
        });
        await using var manager = new HttpServiceManager(store);
        await manager.EnableAsync(true, token);
        var edited = store.GetOrCreate();
        edited.HttpAuthenticationMode = HttpAuthenticationModes.Required;
        edited.HttpBindMode = HttpBindModes.All;
        edited.HttpPort = port == 8877 ? 8878 : 8877;
        edited.HttpServiceEnabled = false;
        store.Save(edited);

        var active = manager.GetStatus();
        Assert.True(active.Running);
        Assert.Equal(HttpAuthenticationModes.None, active.AuthenticationMode);
        Assert.Equal(HttpBindModes.Loopback, active.BindMode);
        Assert.Equal(new[] { "127.0.0.1" }, active.BindAddresses);
        Assert.Equal(port, active.Port);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(active.Endpoint)
        };
        using var request = HttpPolicyTestServer.Request();
        using var response = await client.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var conflict = await Assert.ThrowsAsync<HttpAdministrationException>(() => manager.ConfigureAsync(
            new ConfigureHttpServiceRequest { AuthenticationMode = HttpAuthenticationModes.Required }, token));
        Assert.Equal(ErrorCodes.Conflict, conflict.Code);
        var preferences = await manager.ConfigureAsync(
            new ConfigureHttpServiceRequest { StartupMode = HttpStartupModes.Disabled }, token);
        Assert.Equal(HttpAuthenticationModes.None, preferences.AuthenticationMode);

        var stopped = await manager.DisableAsync(true, token);
        Assert.False(stopped.Running);
        Assert.Equal(HttpAuthenticationModes.Required, stopped.AuthenticationMode);
        Assert.Equal(HttpBindModes.All, stopped.BindMode);
        Assert.Equal(edited.HttpPort, stopped.Port);
    }

    [Fact]
    public async Task StartupPortFailureRemainsVisibleAndCancellationPropagates()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest
        {
            Port = ((IPEndPoint)occupied.LocalEndpoint).Port, StartupMode = HttpStartupModes.Enabled
        });
        await using var manager = new HttpServiceManager(store);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(manager.GetStatus().Enabled);
        Assert.False(manager.GetStatus().Running);
        Assert.NotEmpty(manager.GetStatus().LastError!);
        var failure = manager.GetStatus().LastError;
        var updated = await manager.ConfigureAsync(
            new ConfigureHttpServiceRequest { StartupMode = HttpStartupModes.LastState },
            TestContext.Current.CancellationToken);
        Assert.Equal(failure, updated.LastError);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ApplyStartupAsync(canceled.Token));
    }
}
