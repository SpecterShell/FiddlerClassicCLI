// Exercises shared-port Kestrel bindings, connection tracking, and complete startup rollback.
using System.Net;
using System.Net.Sockets;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FiddlerClassicCLI.Tests;

public sealed class HttpEndpointTransportTests
{
    [Fact]
    public async Task ForegroundIgnoresManagedAnonymousAndAllInterfaceSettings()
    {
        var directory = Directory.CreateTempSubdirectory("FiddlerClassicCLI.HttpPolicy.");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(15));
        Task? running = null;
        try
        {
            var store = new ConfigStore(directory.FullName);
            var configuration = store.GetOrCreate();
            configuration.HttpAuthenticationMode = HttpAuthenticationModes.None;
            configuration.HttpBindMode = HttpBindModes.All;
            store.Save(configuration);
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            running = McpHost.RunHttpAsync(port, store, shutdown.Token);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp")
            };
            while (true)
            {
                try
                {
                    using var missing = HttpPolicyTestServer.Request();
                    using var response = await client.SendAsync(missing, shutdown.Token);
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                    break;
                }
                catch (HttpRequestException) when (!running.IsCompleted)
                {
                    await Task.Delay(10, shutdown.Token);
                }
            }
            using var valid = HttpPolicyTestServer.Request(configuration.HttpBearerToken);
            using var validResponse = await client.SendAsync(valid, shutdown.Token);
            Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);

            using var otherAddress = HttpPolicyTestServer.Request(configuration.HttpBearerToken);
            otherAddress.RequestUri = new Uri($"http://127.0.0.2:{port}/mcp");
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(otherAddress, shutdown.Token));
        }
        finally
        {
            await shutdown.CancelAsync();
            try
            {
                if (running is not null) await running;
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TracksBothLoopbackEndpointsOnOnePort(bool anonymous)
    {
        await using var fixture = await HttpPolicyTestServer.StartAsync(anonymous,
            [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")]);
        foreach (var address in new[] { "127.0.0.1", "127.0.0.2" })
        {
            using var request = HttpPolicyTestServer.Request(anonymous ? null : HttpPolicyTestServer.Credential);
            request.RequestUri = new Uri($"http://{address}:{fixture.Port}/mcp");
            using var response = await fixture.Client.SendAsync(request, fixture.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await fixture.WaitForIdleAsync();
        var records = fixture.Connections.List().Connections;
        Assert.Equal(2, records.Count);
        Assert.Equal(2, records.Select(record => record.ConnectionId).Distinct().Count());
        Assert.All(records, record =>
        {
            Assert.Equal(anonymous ? "anonymous" : "authorized", record.AuthorizationState);
            Assert.Equal(1, record.TotalRequestCount);
            Assert.Equal(0, record.ActiveRequestCount);
            Assert.Equal(anonymous ? 0 : 1, record.ClientIds.Length);
        });

        if (anonymous)
        {
            using var spoofed = HttpPolicyTestServer.Request();
            spoofed.Headers.Host = $"127.0.0.2:{fixture.Port}";
            using var response = await fixture.Client.SendAsync(spoofed, fixture.Token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await fixture.WaitForIdleAsync();
            var rejected = Assert.Single(fixture.Connections.List().Connections,
                record => record.TotalRequestCount == 2);
            Assert.Equal("rejected", rejected.AuthorizationState);
            Assert.Empty(rejected.ClientIds);
            Assert.Empty(rejected.ClientNames);
        }

        Assert.All(records, record => Assert.True(fixture.Connections.Disconnect(record.ConnectionId)));
        while (fixture.Connections.Count != 0)
        {
            await Task.Delay(10, fixture.Token);
        }
    }

    [Fact]
    public async Task SecondBindFailureReleasesTheFirstListener()
    {
        var secondAddress = IPAddress.Parse("127.0.0.2");
        using var conflict = new TcpListener(secondAddress, 0) { ExclusiveAddressUse = true };
        conflict.Start();
        var port = ((IPEndPoint)conflict.LocalEndpoint).Port;
        var connections = new HttpConnectionRegistry();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        await Assert.ThrowsAnyAsync<IOException>(() => McpHost.StartHttpAsync(
            new[] { IPAddress.Loopback, secondAddress }, port,
            new SingleTokenCredentialProvider(HttpPolicyTestServer.Credential), connections, timeout.Token,
            authenticationMode: HttpAuthenticationModes.None));

        // An exclusive bind proves the earlier socket was released, without probing any other process.
        using var reclaimed = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        reclaimed.Start();
        Assert.Equal(0, connections.Count);
    }

    [Fact]
    public async Task CancelledStartupLeavesNoListener()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => McpHost.StartHttpAsync(
            new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") }, port,
            new SingleTokenCredentialProvider(HttpPolicyTestServer.Credential), new HttpConnectionRegistry(),
            cancellation.Token));
        using var reclaimed = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        reclaimed.Start();
    }

    [Fact]
    public async Task EmptyAddressListFailsBeforeStartingAService()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => McpHost.StartHttpAsync(
            Array.Empty<IPAddress>(), 8877, new SingleTokenCredentialProvider(HttpPolicyTestServer.Credential),
            new HttpConnectionRegistry(), TestContext.Current.CancellationToken));
    }

    /// <summary>Disposes application-owned resources even when graceful shutdown fails or is cancelled.</summary>
    /// <param name="cancelShutdown">Exercises cancellation as well as an ordinary shutdown failure.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalStillReleasesApplicationResourcesAfterStopFailure(bool cancelShutdown)
    {
        Exception failure = cancelShutdown
            ? new OperationCanceledException("Synthetic shutdown cancellation.")
            : new InvalidOperationException("Synthetic shutdown failure.");
        var service = new FailingStopService(failure);
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        // Factory registration makes the container own disposal of this test service.
        builder.Services.AddSingleton<IHostedService>(_ => service);
        await using var application = builder.Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await application.StartAsync(timeout.Token);
        var server = new ManagedHttpServer(application, new HttpConnectionRegistry());

        var actual = await Assert.ThrowsAnyAsync<Exception>(() => server.DisposeAsync().AsTask());

        Assert.Same(failure, actual);
        Assert.True(service.Disposed);
    }

    private sealed class FailingStopService(Exception failure) : IHostedService, IAsyncDisposable
    {
        internal bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.FromException(failure);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
