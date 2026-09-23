// Exercises summary MCP invocation over HTTP and stdio against isolated synthetic bridge metadata.
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Protocol;
using ModelContextProtocol.Client;

namespace FiddlerClassicCLI.Tests;

public sealed class SessionSummaryTransportTests
{
    /// <summary>
    /// Calls the registered summary tool over authenticated loopback HTTP without accessing a config store.
    /// </summary>
    /// <param name="protocolVersion">The exact wire revision. Automatic version fallback is disabled.</param>
    [Theory]
    [InlineData("2026-07-28")]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    public async Task HttpSummaryReturnsBoundedStructuredMetadataFromFakePipe(string protocolVersion)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        // In-memory credentials exercise authentication without reading or creating user configuration.
        await using var server = await McpHost.StartHttpAsync(
            IPAddress.Loopback, port, new SingleTokenCredentialProvider("summary-test-token"),
            new HttpConnectionRegistry(), timeout.Token);
        using var handler = new HttpClientHandler { UseProxy = false };
        using var httpClient = new HttpClient(handler);
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer summary-test-token" }
        }, httpClient);
        await using var client = await McpClient.CreateAsync(transport,
            new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: timeout.Token);
        Assert.Equal(protocolVersion, client.NegotiatedProtocolVersion);

        await AssertSummaryInvocationAsync(client, timeout.Token);
    }

    /// <summary>
    /// Calls the registered tool in a stdio host child process with explicit test-only bridge and daemon pipes.
    /// </summary>
    /// <param name="protocolVersion">The exact wire revision. Automatic version fallback is disabled.</param>
    [Theory]
    [InlineData("2026-07-28")]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    public async Task StdioSummaryReturnsBoundedStructuredMetadataFromFakePipe(string protocolVersion)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var executable = Path.Combine(Path.GetDirectoryName(typeof(SessionSummaryTools).Assembly.Location)!, "fiddler-classic-cli.exe");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = executable,
            Arguments = ["mcp", "stdio"],
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["FIDDLER_CLASSIC_PIPE_NAME"] = GetTestPipeName(),
                ["FIDDLER_CLASSIC_DAEMON_PIPE_NAME"] = $"fiddler-classic-cli.summary-daemon-tests.{Guid.NewGuid():N}"
            }
        });
        await using var client = await McpClient.CreateAsync(transport,
            new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: timeout.Token);
        Assert.Equal(protocolVersion, client.NegotiatedProtocolVersion);

        await AssertSummaryInvocationAsync(client, timeout.Token);
    }

    /// <summary>
    /// Serves exactly one list response and checks transport binding, scope, aggregate values, and payload exclusion.
    /// </summary>
    /// <param name="client">An initialized MCP client connected through the transport under test.</param>
    /// <param name="cancellationToken">Bounds the fake pipe exchange and MCP invocation.</param>
    private static async Task AssertSummaryInvocationAsync(McpClient client, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            GetTestPipeName(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var responseTask = ServeSummaryOnceAsync(pipe, exchange.Token);
        try
        {
            var result = await client.CallToolAsync("summarize_network_requests", new Dictionary<string, object?>
            {
                ["minRequestId"] = 10,
                ["maxRequestId"] = 50,
                ["host"] = "example.test",
                ["method"] = "GET",
                ["limit"] = 4,
                ["newestFirst"] = false
            }, cancellationToken: cancellationToken);
            var request = await responseTask;

            Assert.Equal(Operations.ListSessions, request.Operation);
            var filters = JsonSerializer.Deserialize<ListSessionsRequest>(request.PayloadJson)!;
            Assert.Equal(10, filters.MinId);
            Assert.Equal(50, filters.MaxId);
            Assert.Equal("example.test", filters.Host);
            Assert.Equal("GET", filters.Method);
            Assert.Equal(4, filters.Limit);
            Assert.False(filters.NewestFirst);
            Assert.Null(filters.HeaderName);
            Assert.Null(filters.HeaderValue);
            Assert.Null(filters.BodyContains);

            Assert.NotEqual(true, result.IsError);
            Assert.NotNull(result.StructuredContent);
            var summary = JsonSerializer.SerializeToElement(result.StructuredContent);
            Assert.Equal("returned_sessions", summary.GetProperty("scope").GetString());
            Assert.Equal(9, summary.GetProperty("totalMatched").GetInt32());
            Assert.Equal(4, summary.GetProperty("returned").GetInt32());
            Assert.Equal(4, summary.GetProperty("limit").GetInt32());
            Assert.True(summary.GetProperty("truncated").GetBoolean());
            Assert.False(summary.GetProperty("newestFirst").GetBoolean());
            Assert.Equal(11, summary.GetProperty("minReturnedId").GetInt32());
            Assert.Equal(17, summary.GetProperty("maxReturnedId").GetInt32());
            Assert.Equal(3, summary.GetProperty("completedCount").GetInt32());
            Assert.Equal(3_000_000_012L, summary.GetProperty("requestBodyBytes").GetInt64());
            Assert.Equal(100, summary.GetProperty("responseBodyBytes").GetInt64());

            var hosts = summary.GetProperty("byHost").EnumerateArray().ToArray();
            Assert.Equal(new[] { "a.example.test", "b.example.test" }, hosts.Select(host => host.GetProperty("host").GetString()));
            Assert.Equal(new[] { 2, 2 }, hosts.Select(host => host.GetProperty("count").GetInt32()));
            var statuses = summary.GetProperty("byStatus").EnumerateArray().ToArray();
            Assert.Equal(3, statuses.Length);
            // MCP may omit null fields. An unavailable status must not be fabricated as status 0.
            statuses[0].TryGetProperty("statusCode", out var unavailableStatus);
            Assert.True(unavailableStatus.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
            Assert.Equal(200, statuses[1].GetProperty("statusCode").GetInt32());
            Assert.Equal(404, statuses[2].GetProperty("statusCode").GetInt32());
            Assert.Equal(new[] { 1, 2, 1 }, statuses.Select(status => status.GetProperty("count").GetInt32()));

            var timings = summary.GetProperty("durationMilliseconds");
            Assert.Equal(2, timings.GetProperty("count").GetInt32());
            Assert.Equal(10, timings.GetProperty("min").GetDouble());
            Assert.Equal(30, timings.GetProperty("max").GetDouble());
            Assert.Equal(20, timings.GetProperty("median").GetDouble());
            Assert.Equal(30, timings.GetProperty("p95").GetDouble());
            Assert.False(summary.TryGetProperty("sessions", out _));
            Assert.False(summary.TryGetProperty("requests", out _));
            Assert.False(summary.TryGetProperty("headers", out _));
            Assert.False(summary.TryGetProperty("body", out _));
        }
        finally
        {
            // Cancel and observe the listener even when discovery, binding, or the tool call fails.
            await exchange.CancelAsync();
            try
            {
                await responseTask;
            }
            catch (OperationCanceledException) when (exchange.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>
    /// Reuses the shared response envelope builder with a cancellable pipe owned by this test.
    /// </summary>
    /// <param name="pipe">The single-connection fake bridge pipe. The caller owns disposal.</param>
    /// <param name="cancellationToken">Cancels acceptance and framed I/O, including assertion-failure cleanup.</param>
    /// <returns>The sole observed bridge request. No second connection or payload operation is served.</returns>
    private static async Task<BridgeRequest> ServeSummaryOnceAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await pipe.WaitForConnectionAsync(cancellationToken);
        var requestJson = await FrameCodec.ReadAsync(pipe, cancellationToken);
        var request = JsonSerializer.Deserialize<BridgeRequest>(requestJson!)!;
        var response = FakePipeServer.Success(request, new ListSessionsResponse
        {
            TotalMatched = 9,
            Sessions =
            [
                new() { Id = 11, Host = "a.example.test", Method = "GET", StatusCode = 200,
                    RequestBodyBytes = 3_000_000_000, ResponseBodyBytes = 10, IsComplete = true, DurationMilliseconds = 30 },
                new() { Id = 13, Host = "A.EXAMPLE.TEST", Method = "GET", StatusCode = 200,
                    RequestBodyBytes = 2, ResponseBodyBytes = 20, IsComplete = true, DurationMilliseconds = 10 },
                new() { Id = 15, Host = "b.example.test", Method = "GET", StatusCode = 404,
                    RequestBodyBytes = 4, ResponseBodyBytes = 30, IsComplete = true },
                new() { Id = 17, Host = "b.example.test", Method = "GET",
                    RequestBodyBytes = 6, ResponseBodyBytes = 40, IsComplete = false, DurationMilliseconds = 999 }
            ]
        });
        await FrameCodec.WriteAsync(pipe, JsonSerializer.Serialize(response), cancellationToken);
        return request;
    }

    private static string GetTestPipeName()
    {
        var pipeName = PipeNames.ForCurrentUser();
        // AssemblyInfo supplies a process-unique override. Fail closed if another test removed it.
        Assert.StartsWith($"fiddler-classic-cli.tests.{Environment.ProcessId}.", pipeName, StringComparison.Ordinal);
        return pipeName;
    }
}
