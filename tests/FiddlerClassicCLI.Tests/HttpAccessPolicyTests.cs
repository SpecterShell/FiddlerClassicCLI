// Verifies explicit anonymous HTTP access, DNS-rebinding defenses, and credential-free metadata.
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FiddlerClassicCLI.Host.Mcp;
using Microsoft.AspNetCore.Http;

using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class HttpAccessPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothOverloadsRequireAuthenticationByDefault(bool legacyOverload)
    {
        await using var fixture = await HttpPolicyTestServer.StartAsync(legacyOverload: legacyOverload);
        using var missing = HttpPolicyTestServer.Request();
        using var missingResponse = await fixture.Client.SendAsync(missing, fixture.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, missingResponse.StatusCode);
        Assert.Contains(missingResponse.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
        Assert.Equal("rejected", Assert.Single(fixture.Connections.List().Connections).AuthorizationState);

        using var valid = HttpPolicyTestServer.Request(HttpPolicyTestServer.Credential);
        using var validResponse = await fixture.Client.SendAsync(valid, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        var record = Assert.Single(fixture.Connections.List().Connections);
        Assert.Equal("mixed", record.AuthorizationState);
        Assert.Single(record.ClientIds);
        Assert.Single(record.ClientNames);
        Assert.Equal(2, fixture.Credentials.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("irrelevant-invalid-token")]
    [InlineData(HttpPolicyTestServer.Credential)]
    public async Task ExplicitAnonymousModeNeverUsesOrAttributesCredentials(string? token)
    {
        await using var fixture = await HttpPolicyTestServer.StartAsync(anonymous: true);
        using var request = HttpPolicyTestServer.Request(token);
        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await McpHttpTestServer.ReadResponseAsync(response, fixture.Token);
        Assert.True(body.GetProperty("result").TryGetProperty("supportedVersions", out _));
        Assert.False(response.Headers.Contains("WWW-Authenticate"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(0, fixture.Credentials.Calls);
        await fixture.WaitForIdleAsync();
        var record = Assert.Single(fixture.Connections.List().Connections);
        Assert.Equal("anonymous", record.AuthorizationState);
        Assert.Empty(record.ClientIds);
        Assert.Empty(record.ClientNames);
        Assert.Equal(1, record.TotalRequestCount);
        Assert.Equal(0, record.ActiveRequestCount);
        Assert.False(string.IsNullOrEmpty(record.RemoteEndpoint));
        Assert.False(string.IsNullOrEmpty(record.LastActivityAtUtc));
        Assert.DoesNotContain(HttpPolicyTestServer.Credential, JsonSerializer.Serialize(record));
        Assert.DoesNotContain("irrelevant-invalid-token", JsonSerializer.Serialize(record));
    }

    [Theory]
    [InlineData("https://untrusted.example")]
    [InlineData("http://localhost")]
    [InlineData("null")]
    [InlineData("invalid-origin")]
    [InlineData("")]
    public async Task AnonymousRequestsStillRejectEveryOrigin(string origin)
    {
        await using var fixture = await HttpPolicyTestServer.StartAsync(anonymous: true);
        using var request = HttpPolicyTestServer.Request();
        Assert.True(request.Headers.TryAddWithoutValidation("Origin", origin));
        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(fixture.Token));
        Assert.Equal(0, fixture.Credentials.Calls);
        await fixture.WaitForIdleAsync();
        var record = Assert.Single(fixture.Connections.List().Connections);
        Assert.Equal("rejected", record.AuthorizationState);
        Assert.Equal(1, record.TotalRequestCount);
        Assert.Equal(0, record.ActiveRequestCount);
        Assert.Empty(record.ClientIds);
        Assert.Empty(record.ClientNames);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::ffff:127.0.0.1]", true)]
    [InlineData("rebound.example", false)]
    [InlineData("localhost.example", false)]
    [InlineData("localhost.", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.2", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("[::1]", false)]
    public async Task AnonymousHostMustMatchTheReceivingAddress(string host, bool allowed)
    {
        await using var fixture = await HttpPolicyTestServer.StartAsync(anonymous: true);
        using var request = HttpPolicyTestServer.Request();
        request.Headers.Host = $"{host}:{fixture.Port}";
        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        await fixture.WaitForIdleAsync();
        var record = Assert.Single(fixture.Connections.List().Connections);
        Assert.Equal(allowed ? "anonymous" : "rejected", record.AuthorizationState);
        Assert.Equal(1, record.TotalRequestCount);
        Assert.Equal(0, record.ActiveRequestCount);
        Assert.Empty(record.ClientIds);
        Assert.Empty(record.ClientNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousHostRejectsMissingOrDifferentNonDefaultPort(bool omitPort)
    {
        await using var fixture = await HttpPolicyTestServer.StartAsync(anonymous: true);
        using var request = HttpPolicyTestServer.Request();
        request.Headers.Host = omitPort ? "127.0.0.1" : "127.0.0.1:1";
        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", 80, true)]
    [InlineData("localhost", "127.0.0.2", 80, true)]
    [InlineData("localhost:8877", "192.0.2.1", 8877, false)]
    [InlineData("192.0.2.1:8877", "192.0.2.1", 8877, true)]
    [InlineData("localhost:8877", "::ffff:127.0.0.1", 8877, true)]
    [InlineData("127.0.0.1:8877", "::ffff:127.0.0.1", 8877, true)]
    [InlineData("[::ffff:127.0.0.1]:8877", "127.0.0.1", 8877, true)]
    [InlineData("[::1]", "::1", 80, true)]
    [InlineData("[::1]:8877", "::1", 8877, true)]
    [InlineData("[::]:8877", "::", 8877, false)]
    [InlineData("0.0.0.0:8877", "0.0.0.0", 8877, false)]
    [InlineData("[::1]suffix", "::1", 80, false)]
    [InlineData("[::1]:", "::1", 80, false)]
    [InlineData("[::1]:invalid", "::1", 80, false)]
    [InlineData("localhost:", "127.0.0.1", 80, false)]
    [InlineData("127.0.0.1:invalid", "127.0.0.1", 80, false)]
    [InlineData("127.0.0.1:+80", "127.0.0.1", 80, false)]
    [InlineData("127.0.0.1:999999999999", "127.0.0.1", 80, false)]
    [InlineData("127.0.0.1", "127.0.0.1", 8877, false)]
    [InlineData("127.0.0.1:8877", null, 8877, false)]
    [InlineData("", "127.0.0.1", 80, false)]
    public void HostPolicyNormalizesMappedAddressesAndValidatesCompleteAuthority(
        string host, string? localAddress, int port, bool allowed)
    {
        Assert.Equal(allowed, HttpAccessPolicy.IsAllowedAnonymousHost(
            new HostString(host), localAddress is null ? null : IPAddress.Parse(localAddress), port));
    }

    [Fact]
    public async Task RequestDataIsAbsentFromTransportLogsAndConnectionRecords()
    {
        const string secret = "test-only-header-canary";
        const string query = "test-only-query-canary";
        using var logs = new StringWriter();
        var originalError = Console.Error;
        var originalOutput = Console.Out;
        string records;
        Console.SetError(logs);
        Console.SetOut(logs);
        try
        {
            await using var fixture = await HttpPolicyTestServer.StartAsync(anonymous: true);
            using var request = HttpPolicyTestServer.Request(secret);
            request.RequestUri = new Uri($"/mcp?{query}", UriKind.Relative);
            request.Headers.Add("X-Test-Secret", secret);
            using var response = await fixture.Client.SendAsync(request, fixture.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            records = JsonSerializer.Serialize(fixture.Connections.List());
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOutput);
        }
        Assert.DoesNotContain(secret, records);
        Assert.DoesNotContain(query, records);
        Assert.DoesNotContain(secret, logs.ToString());
        Assert.DoesNotContain(query, logs.ToString());
        Assert.DoesNotContain("/mcp", logs.ToString());
    }
}

internal sealed class HttpPolicyTestServer : IAsyncDisposable
{
    internal const string Credential = "access-policy-test-only";
    private readonly ManagedHttpServer _server;
    private readonly CancellationTokenSource _timeout;
    internal HttpClient Client { get; }
    internal int Port { get; }
    internal HttpConnectionRegistry Connections { get; }
    internal CountingCredentials Credentials { get; }
    internal CancellationToken Token => _timeout.Token;

    private HttpPolicyTestServer(ManagedHttpServer server, CancellationTokenSource timeout, int port,
        HttpConnectionRegistry connections, CountingCredentials credentials)
    {
        _server = server;
        _timeout = timeout;
        Port = port;
        Connections = connections;
        Credentials = credentials;
        Client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp"),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    /// <summary>Starts isolated real sockets with in-memory credentials and a bounded lifetime.</summary>
    /// <param name="anonymous">Explicitly opts out of bearer authentication.</param>
    /// <param name="addresses">Optional local bindings, defaulting to IPv4 loopback.</param>
    /// <param name="legacyOverload">Exercises the existing single-address signature.</param>
    /// <param name="authenticationMode">Optional explicit policy for the shared multi-address server.</param>
    /// <returns>A fixture owning the HTTP service and its client.</returns>
    internal static async Task<HttpPolicyTestServer> StartAsync(bool anonymous = false,
        IPAddress[]? addresses = null, bool legacyOverload = false, string? authenticationMode = null)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var connections = new HttpConnectionRegistry();
            var credentials = new CountingCredentials();
            addresses ??= [IPAddress.Loopback];
            var server = legacyOverload
                ? await McpHost.StartHttpAsync(addresses[0], port, credentials, connections, timeout.Token)
                : await McpHost.StartHttpAsync(addresses, port, credentials, connections, timeout.Token,
                    authenticationMode: authenticationMode ?? (anonymous ? HttpAuthenticationModes.None : HttpAuthenticationModes.Required));
            return new HttpPolicyTestServer(server, timeout, port, connections, credentials);
        }
        catch
        {
            timeout.Dispose();
            throw;
        }
    }

    internal static HttpRequestMessage Request(string? token = null)
    {
        var request = McpHttpTestServer.Request("server/discover");
        request.Headers.Authorization = token is null ? null : new("Bearer", token);
        return request;
    }

    internal async Task WaitForIdleAsync()
    {
        while (Connections.List().Connections.Any(connection => connection.ActiveRequestCount != 0))
        {
            await Task.Delay(10, Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await _server.DisposeAsync();
        }
        finally
        {
            _timeout.Dispose();
        }
    }

    internal sealed class CountingCredentials : IHttpCredentialProvider
    {
        private readonly SingleTokenCredentialProvider _provider = new(Credential);
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public HttpClientIdentity? Authenticate(string? authorization)
        {
            Interlocked.Increment(ref _calls);
            return _provider.Authenticate(authorization);
        }
    }
}
