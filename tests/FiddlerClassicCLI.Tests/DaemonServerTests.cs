// Verifies daemon request relay, status reporting, and orderly shutdown.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Daemon;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class DaemonServerTests
{
    [Theory]
    [InlineData(99, "rejected-stop", ErrorCodes.ProtocolMismatch)]
    [InlineData(DaemonProtocol.Version, "", ErrorCodes.InvalidRequest)]
    [InlineData(DaemonProtocol.Version, " ", ErrorCodes.InvalidRequest)]
    public async Task RejectedStopRequestsLeaveTheDaemonRunning(int version, string requestId, string errorCode)
    {
        var pipeName = "fiddler-classic-daemon-test-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "FiddlerClassicCLITests", Guid.NewGuid().ToString("N"));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(10));
        var serverTask = new DaemonServer(pipeName, TimeSpan.FromSeconds(1), new ConfigStore(directory)).RunAsync(lifetime.Token);
        var client = new DaemonClient("unused.exe", pipeName, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        try
        {
            await WaitForStatus(client, lifetime.Token);
            var json = await NamedPipeFrameClient.ExchangeAsync(pipeName, JsonSerializer.Serialize(new DaemonRequest
            {
                ProtocolVersion = version,
                RequestId = requestId,
                Method = DaemonProtocol.Stop
            }), lifetime.Token);
            var response = JsonSerializer.Deserialize<DaemonResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.False(response.Success);
            Assert.Equal(errorCode, response.Error!.Code);

            Assert.NotNull(await client.TryGetStatusAsync(lifetime.Token));
            Assert.True((await client.StopAsync(lifetime.Token)).WasRunning);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
        }
        finally
        {
            lifetime.Cancel();
            try { await serverTask; }
            catch (OperationCanceledException) { }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies status, bridge relay, stop acknowledgment, and final pipe shutdown as one daemon lifecycle.
    /// </summary>
    [Fact]
    public async Task RelaysBridgeCallsAndStopsCleanly()
    {
        var pipeName = DaemonPipeNames.ForCurrentUser();
        var configDirectory = Path.Combine(Path.GetTempPath(), "FiddlerClassicCLITests", Guid.NewGuid().ToString("N"));
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var server = new DaemonServer(pipeName, TimeSpan.FromSeconds(3), new ConfigStore(configDirectory));
        var serverTask = server.RunAsync(cancellationSource.Token);
        var daemonClient = new DaemonClient(
            "unused.exe",
            pipeName,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3));

        try
        {
            var status = await WaitForStatus(daemonClient, TestContext.Current.CancellationToken);
            Assert.True(status.Running);
            Assert.Equal(Environment.ProcessId, status.ProcessId);
            Assert.Contains(DaemonProtocol.ManagedHttpCapability, status.Capabilities);
            Assert.NotNull(status.HttpService);

            var httpPort = GetFreePort();
            var configured = await daemonClient.ConfigureHttpServiceAsync(
                new ConfigureHttpServiceRequest { BindMode = HttpBindModes.Loopback, Port = httpPort },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpBindModes.Loopback, configured.BindMode);
            Assert.Equal(httpPort, configured.Port);
            Assert.Equal(httpPort, (await daemonClient.GetHttpServiceStatusAsync(
                TestContext.Current.CancellationToken)).Port);

            var enabled = await daemonClient.EnableHttpServiceAsync(
                confirm: false,
                TestContext.Current.CancellationToken);
            Assert.True(enabled.Running);

            var authorized = await daemonClient.AuthorizeHttpClientAsync(
                "Daemon test",
                TestContext.Current.CancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(authorized.Token));
            var clients = await daemonClient.ListHttpClientsAsync(TestContext.Current.CancellationToken);
            Assert.Contains(clients.Clients, client => client.ClientId == authorized.Client.ClientId);

            using (var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") })
            using (var httpResponse = await SendInitializeAsync(
                       httpClient,
                       authorized.Token,
                       TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
                var connections = await daemonClient.ListHttpConnectionsAsync(TestContext.Current.CancellationToken);
                var connection = Assert.Single(connections.Connections);
                Assert.Contains(authorized.Client.ClientId, connection.ClientIds);

                var disconnected = await daemonClient.DisconnectHttpConnectionAsync(
                    connection.ConnectionId,
                    confirm: true,
                    TestContext.Current.CancellationToken);
                Assert.True(disconnected.Disconnected);
            }

            var deauthorized = await daemonClient.DeauthorizeHttpClientAsync(
                authorized.Client.ClientId,
                confirm: true,
                TestContext.Current.CancellationToken);
            Assert.Equal(authorized.Client.ClientId, deauthorized.ClientId);

            var disabled = await daemonClient.DisableHttpServiceAsync(
                confirm: true,
                TestContext.Current.CancellationToken);
            Assert.False(disabled.Enabled);
            Assert.False(disabled.Running);

            var bridgeTask = FakePipeServer.ServeOnceAsync(request => FakePipeServer.Success(
                request,
                new CaptureResponse { IsProxyAttached = true }));
            var bridgeClient = new DaemonBridgeClient(daemonClient);
            var response = await bridgeClient.SendAsync<SetCaptureRequest, CaptureResponse>(
                Operations.SetCapture,
                new SetCaptureRequest { Enabled = true },
                TestContext.Current.CancellationToken);
            var request = await bridgeTask;

            Assert.True(response.IsProxyAttached);
            Assert.Equal(Operations.SetCapture, request.Operation);

            var stop = await daemonClient.StopAsync(TestContext.Current.CancellationToken);
            Assert.True(stop.WasRunning);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Null(await daemonClient.TryGetStatusAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            cancellationSource.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }

            if (Directory.Exists(configDirectory))
            {
                Directory.Delete(configDirectory, recursive: true);
            }
        }
    }

    private static async Task<HttpResponseMessage> SendInitializeAsync(
        HttpClient client,
        string token,
        CancellationToken cancellationToken)
    {
        const string payload = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"daemon-tests","version":"1.0"}}}
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

    /// <summary>
    /// Polls until the test daemon accepts status requests or the bounded attempt count expires.
    /// </summary>
    /// <param name="client">The daemon client used for status probes.</param>
    /// <param name="cancellationToken">Cancels probes and polling delays.</param>
    private static async Task<DaemonStatus> WaitForStatus(
        DaemonClient client,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var status = await client.TryGetStatusAsync(cancellationToken);
            if (status is not null)
            {
                return status;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException("The test CLI daemon did not start.");
    }
}
