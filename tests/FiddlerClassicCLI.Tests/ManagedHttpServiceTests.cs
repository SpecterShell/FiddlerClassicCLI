// Exercises managed listener binding, named-client authorization, connection visibility, and revocation.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed partial class ManagedHttpServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FiddlerClassicCLITests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BindsAllIpv4InterfacesAndRevokesNamedClientImmediately()
    {
        var port = GetFreePort();
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { BindMode = HttpBindModes.All, Port = port,
            AuthenticationMode = HttpAuthenticationModes.Required });
        await using var manager = new HttpServiceManager(store);
        var authorized = manager.AuthorizeClient(new AuthorizeHttpClientRequest { Name = "Integration test" });

        var status = await manager.EnableAsync(confirmRemote: true, TestContext.Current.CancellationToken);
        Assert.True(status.Running);
        Assert.Equal("0.0.0.0", status.BindAddress);
        Assert.Equal($"http://0.0.0.0:{port}/mcp", status.Endpoint);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var accepted = await SendInitialize(client, authorized.Token, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var connections = manager.ListConnections();
        Assert.NotEmpty(connections.Connections);
        Assert.Contains(connections.Connections, connection =>
            connection.ClientIds.Contains(authorized.Client.ClientId, StringComparer.Ordinal));
        Assert.DoesNotContain(authorized.Token, JsonSerializer.Serialize(connections), StringComparison.Ordinal);
        Assert.DoesNotContain(authorized.Token, JsonSerializer.Serialize(manager.ListClients()), StringComparison.Ordinal);

        var revoked = manager.DeauthorizeClient(new DeauthorizeHttpClientRequest
        {
            ClientId = authorized.Client.ClientId,
            Confirm = true
        });
        Assert.True(revoked.DisconnectedConnectionCount >= 1);

        using var retryClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var rejected = await SendInitialize(retryClient, authorized.Token, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.DoesNotContain(authorized.Token, File.ReadAllText(store.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteEnableRequiresConfirmation()
    {
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { BindMode = HttpBindModes.All, Port = GetFreePort() });
        await using var manager = new HttpServiceManager(store);

        var exception = await Assert.ThrowsAsync<HttpAdministrationException>(() =>
            manager.EnableAsync(confirmRemote: false, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ConfirmationRequired, exception.Code);
        Assert.False(store.GetOrCreate().HttpServiceEnabled);
    }

    [Fact]
    public async Task ReloadsTheDefaultCredentialWithoutRestartingTheListener()
    {
        var port = GetFreePort();
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { BindMode = HttpBindModes.Loopback, Port = port,
            AuthenticationMode = HttpAuthenticationModes.Required });
        var initialToken = store.GetOrCreate().HttpBearerToken;
        await using var manager = new HttpServiceManager(store);
        await manager.EnableAsync(confirmRemote: false, TestContext.Current.CancellationToken);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var initial = await SendInitialize(client, initialToken, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);

        var rotatedToken = store.RotateToken().HttpBearerToken;
        using var rejected = await SendInitialize(client, initialToken, TestContext.Current.CancellationToken);
        using var accepted = await SendInitialize(client, rotatedToken, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True(manager.GetStatus().Running);
    }

    [Fact]
    public async Task StartsSavedListenerStateDuringDaemonInitialization()
    {
        var port = GetFreePort();
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { BindMode = HttpBindModes.Loopback, Port = port });
        store.SetHttpServiceEnabled(true);
        await using var manager = new HttpServiceManager(store);

        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(manager.GetStatus().Running);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var response = await SendInitialize(
            client,
            store.GetOrCreate().HttpBearerToken,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task KeepsSavedEnablementAndReportsPortConflicts()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { BindMode = HttpBindModes.Loopback, Port = port });
        await using var manager = new HttpServiceManager(store);

        var exception = await Assert.ThrowsAsync<HttpAdministrationException>(() =>
            manager.EnableAsync(confirmRemote: false, TestContext.Current.CancellationToken));
        var status = manager.GetStatus();

        Assert.Equal(ErrorCodes.Unavailable, exception.Code);
        Assert.True(status.Enabled);
        Assert.False(status.Running);
        Assert.False(string.IsNullOrWhiteSpace(status.LastError));
    }

    private static async Task<HttpResponseMessage> SendInitialize(
        HttpClient client,
        string token,
        CancellationToken cancellationToken)
    {
        const string payload = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"tests","version":"1.0"}}}
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
