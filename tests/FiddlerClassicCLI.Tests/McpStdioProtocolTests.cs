// Verifies modern and legacy MCP newline JSON-RPC without SDK client negotiation or fallback.
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Protocol;
using ModelContextProtocol.Server;

namespace FiddlerClassicCLI.Tests;

public sealed class McpStdioProtocolTests
{
    private const string ModernVersion = "2026-07-28";

    /// <summary>
    /// Uses per-request metadata from the first line, never sending initialize or initialized.
    /// </summary>
    /// <param name="discoverFirst">Whether to discover before listing. False proves discovery is optional.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModernRequestsPreserveToolsAndStructuredResultsWithoutInitialize(bool discoverFirst)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var pipeName = $"fiddler-classic-cli.stdio-wire-tests.{Guid.NewGuid():N}";
        using var process = StartHost(pipeName);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            if (discoverFirst)
            {
                var discovery = await RequestAsync(process, 1, "server/discover",
                    new { _meta = ModernMetadata(ModernVersion) }, timeout.Token);
                AssertDiscovery(AssertResult(discovery, modern: true));
            }

            var tools = await RequestAsync(process, 2, "tools/list",
                new { _meta = ModernMetadata(ModernVersion) }, timeout.Token);
            AssertTools(AssertResult(tools, modern: true));
            await AssertListCallAsync(process, pipeName, modern: true, timeout.Token);
        }
        finally
        {
            await StopHostAsync(process, stderr);
        }
    }

    /// <summary>
    /// Negotiates each legacy revision explicitly before listing and calling the same public tools.
    /// </summary>
    /// <param name="version">The legacy revision that must be echoed by initialize.</param>
    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    public async Task LegacyInitializeNegotiatesRequestedVersionAndPreservesToolResults(string version)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var pipeName = $"fiddler-classic-cli.stdio-wire-tests.{Guid.NewGuid():N}";
        using var process = StartHost(pipeName);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            var initialize = await RequestAsync(process, 1, "initialize", new
            {
                protocolVersion = version,
                capabilities = new { },
                clientInfo = new { name = "stdio-wire-tests", version = "1.0" }
            }, timeout.Token);
            var result = AssertResult(initialize, modern: false);
            Assert.Equal(version, result.GetProperty("protocolVersion").GetString());
            Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("tools").ValueKind);
            AssertServerInfo(result.GetProperty("serverInfo"));

            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), timeout.Token);
            var tools = await RequestAsync(process, 2, "tools/list", new { }, timeout.Token);
            AssertTools(AssertResult(tools, modern: false));
            await AssertListCallAsync(process, pipeName, modern: false, timeout.Token);
        }
        finally
        {
            await StopHostAsync(process, stderr);
        }
    }

    /// <summary>
    /// Rejects an unknown per-request revision and permits a modern retry on the same uninitialized process.
    /// </summary>
    /// <param name="method">The first method sent, covering discovery and direct tool listing.</param>
    [Theory]
    [InlineData("server/discover")]
    [InlineData("tools/list")]
    public async Task UnsupportedVersionReturnsModernErrorWithoutLegacyFallback(string method)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = StartHost($"fiddler-classic-cli.stdio-wire-tests.{Guid.NewGuid():N}");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            const string unsupportedVersion = "1900-01-01";
            var response = await RequestAsync(process, 1, method,
                new { _meta = ModernMetadata(unsupportedVersion) }, timeout.Token);
            Assert.False(response.TryGetProperty("result", out _));
            var error = response.GetProperty("error");
            Assert.Equal(-32022, error.GetProperty("code").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
            var data = error.GetProperty("data");
            Assert.Equal(unsupportedVersion, data.GetProperty("requested").GetString());
            var supported = data.GetProperty("supported").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains(ModernVersion, supported);
            Assert.DoesNotContain(unsupportedVersion, supported);

            var retry = await RequestAsync(process, 2, method,
                new { _meta = ModernMetadata(ModernVersion) }, timeout.Token);
            var result = AssertResult(retry, modern: true);
            if (method == "server/discover")
            {
                AssertDiscovery(result);
            }
            else
            {
                AssertTools(result);
            }
        }
        finally
        {
            await StopHostAsync(process, stderr);
        }
    }

    /// <summary>Launches only the built stdio host, with no access to real bridge or daemon pipes.</summary>
    /// <param name="pipeName">The unique fake bridge pipe owned by this test.</param>
    private static Process StartHost(string pipeName)
    {
        var executable = HostExecutable.Resolve();
        var startInfo = new ProcessStartInfo(executable, "mcp stdio")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Override inherited values locally. AssemblyInfo's process-wide test pipes are not shared here.
        startInfo.Environment["FIDDLER_CLASSIC_PIPE_NAME"] = pipeName;
        startInfo.Environment["FIDDLER_CLASSIC_DAEMON_PIPE_NAME"] = $"fiddler-classic-cli.stdio-wire-daemon-tests.{Guid.NewGuid():N}";
        // The stdio command and metadata-only tool do not load or create persisted configuration.
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the stdio test host.");
    }

    private static Dictionary<string, object> ModernMetadata(string version) => new()
    {
        ["io.modelcontextprotocol/protocolVersion"] = version,
        ["io.modelcontextprotocol/clientCapabilities"] = new { },
        ["io.modelcontextprotocol/clientInfo"] = new { name = "stdio-wire-tests", version = "1.0" }
    };

    /// <summary>Sends one newline-delimited request and requires the next stdout line to be its response.</summary>
    /// <param name="process">The child process whose streams are owned by the test.</param>
    /// <param name="id">The expected response correlation ID.</param>
    /// <param name="method">The literal MCP method. No negotiation or retry is performed here.</param>
    /// <param name="parameters">Plain JSON parameters, independent of SDK wire DTOs.</param>
    /// <param name="cancellationToken">Bounds both writes and the response read.</param>
    private static async Task<JsonElement> RequestAsync(
        Process process, int id, string method, object parameters, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        var response = document.RootElement;
        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.Equal(id, response.GetProperty("id").GetInt32());
        Assert.False(response.TryGetProperty("method", out _));
        Assert.True(response.TryGetProperty("result", out _) ^ response.TryGetProperty("error", out _));
        return response.Clone();
    }

    private static JsonElement AssertResult(JsonElement response, bool modern)
    {
        Assert.False(response.TryGetProperty("error", out _));
        var result = response.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        // An absent resultType must not silently pass as a modern "complete" result.
        if (modern || result.TryGetProperty("resultType", out _))
        {
            Assert.Equal("complete", result.GetProperty("resultType").GetString());
        }
        return result;
    }

    private static void AssertDiscovery(JsonElement result)
    {
        Assert.Contains(ModernVersion, result.GetProperty("supportedVersions").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("tools").ValueKind);
        AssertServerInfo(result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo"));
        AssertCachingHints(result);
    }

    private static void AssertServerInfo(JsonElement serverInfo)
    {
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("version").GetString()));
    }

    private static void AssertCachingHints(JsonElement result)
    {
        if (result.TryGetProperty("ttlMs", out var ttl))
        {
            Assert.Equal(JsonValueKind.Number, ttl.ValueKind);
            Assert.True(ttl.TryGetInt64(out var milliseconds) && milliseconds >= 0);
        }
        if (result.TryGetProperty("cacheScope", out var scope))
        {
            Assert.Contains(scope.GetString(), new[] { "public", "private" });
        }
    }

    /// <summary>Checks the full approved catalog and compares wire safety hints with existing declarations.</summary>
    /// <param name="result">The raw tools/list result for either era.</param>
    private static void AssertTools(JsonElement result)
    {
        var expectedNames = new[]
        {
            "get_status", "start_capture", "stop_capture", "list_network_requests", "wait_for_network_request",
            "get_network_request", "get_network_request_body", "clear_network_requests", "remove_network_requests",
            "save_network_archive", "load_network_archive", "replay_network_request", "send_request",
            "diff_network_requests", "list_websocket_messages", "get_websocket_message", "summarize_network_requests",
            "get_autoresponder_status", "configure_autoresponder", "list_autoresponder_rules", "add_autoresponder_rule",
            "update_autoresponder_rule", "move_autoresponder_rule", "remove_autoresponder_rule", "clear_autoresponder_rules",
            "save_autoresponder_rules", "load_autoresponder_rules", "list_network_breakpoint_arms", "arm_network_breakpoint",
            "disarm_network_breakpoint", "list_pending_network_breakpoints", "wait_for_network_breakpoint",
            "get_network_breakpoint", "update_network_breakpoint", "resume_network_breakpoint", "abort_network_breakpoint"
        };
        var declarations = new[] { typeof(FiddlerTools), typeof(SessionSummaryTools), typeof(AutoResponderTools), typeof(BreakpointTools) }
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .OfType<McpServerToolAttribute>()
            .ToDictionary(attribute => attribute.Name!);
        var tools = result.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(36, tools.Length);
        Assert.Equal(expectedNames.Order(), tools.Select(tool => tool.GetProperty("name").GetString()).Order());
        foreach (var tool in tools)
        {
            var declaration = declarations[tool.GetProperty("name").GetString()!];
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
            Assert.Equal(JsonValueKind.Object, tool.GetProperty("inputSchema").ValueKind);
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
            Assert.True(declaration.UseStructuredContent);
            Assert.Equal(JsonValueKind.Object, tool.GetProperty("outputSchema").ValueKind);
            var annotations = tool.GetProperty("annotations");
            Assert.Equal(declaration.ReadOnly, annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.Equal(declaration.Destructive, Annotation(annotations, "destructiveHint", defaultValue: true));
            Assert.Equal(declaration.Idempotent, Annotation(annotations, "idempotentHint", defaultValue: false));
            Assert.Equal(declaration.OpenWorld, Annotation(annotations, "openWorldHint", defaultValue: true));
        }
        AssertCachingHints(result);
    }

    private static bool Annotation(JsonElement annotations, string name, bool defaultValue) =>
        annotations.TryGetProperty(name, out var value) ? value.GetBoolean() : defaultValue;

    /// <summary>Calls one bounded list operation against a cancellable, current-user-only synthetic pipe.</summary>
    /// <param name="process">The raw stdio child process.</param>
    /// <param name="pipeName">The child's unique test-only bridge pipe.</param>
    /// <param name="modern">Whether every request requires modern metadata and resultType.</param>
    /// <param name="cancellationToken">Bounds the entire exchange, including accepting a connection.</param>
    private static async Task AssertListCallAsync(Process process, string pipeName, bool modern, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bridgeTask = ServeListOnceAsync(pipe, exchange.Token);
        try
        {
            var parameters = new Dictionary<string, object>
            {
                ["name"] = "list_network_requests",
                ["arguments"] = new { minRequestId = 20, maxRequestId = 30, host = "127.0.0.1", method = "GET", pageSize = 1, newestFirst = false }
            };
            if (modern)
            {
                parameters["_meta"] = ModernMetadata(ModernVersion);
            }
            var response = await RequestAsync(process, 3, "tools/call", parameters, cancellationToken);
            var request = await bridgeTask;
            Assert.Equal(Operations.ListSessions, request.Operation);
            var filters = JsonSerializer.Deserialize<ListSessionsRequest>(request.PayloadJson)!;
            Assert.Equal(20, filters.MinId);
            Assert.Equal(30, filters.MaxId);
            Assert.Equal("127.0.0.1", filters.Host);
            Assert.Equal("GET", filters.Method);
            Assert.Equal(1, filters.Limit);
            Assert.False(filters.NewestFirst);
            Assert.Null(filters.HeaderName);
            Assert.Null(filters.HeaderValue);
            Assert.Null(filters.BodyContains);

            var result = AssertResult(response, modern);
            Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
            var structured = result.GetProperty("structuredContent");
            Assert.Equal(2, structured.GetProperty("totalMatched").GetInt32());
            Assert.Equal(1, structured.GetProperty("returned").GetInt32());
            Assert.True(structured.GetProperty("hasMore").GetBoolean());
            Assert.Equal(24, structured.GetProperty("nextMinRequestId").GetInt32());
            Assert.False(structured.TryGetProperty("sessions", out _));
            var item = Assert.Single(structured.GetProperty("requests").EnumerateArray());
            Assert.Equal(23, item.GetProperty("requestId").GetInt32());
            Assert.Equal("GET", item.GetProperty("method").GetString());
            Assert.Equal("http://127.0.0.1/stdio-wire-test", item.GetProperty("url").GetString());
            Assert.Equal(200, item.GetProperty("statusCode").GetInt32());
            Assert.Equal(3_000_000_012L, item.GetProperty("responseBodyBytes").GetInt64());
            Assert.True(item.GetProperty("isComplete").GetBoolean());
            Assert.False(item.TryGetProperty("headers", out _));
            Assert.False(item.TryGetProperty("body", out _));
            var content = Assert.Single(result.GetProperty("content").EnumerateArray());
            Assert.Equal("text", content.GetProperty("type").GetString());
            using var text = JsonDocument.Parse(content.GetProperty("text").GetString()!);
            Assert.True(JsonElement.DeepEquals(structured, text.RootElement));
        }
        finally
        {
            await exchange.CancelAsync();
            try
            {
                await bridgeTask;
            }
            catch (OperationCanceledException) when (exchange.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Reuses the fake peer's envelope builder without its assembly-global pipe or unbounded lifetime.</summary>
    /// <param name="pipe">The single-connection pipe. The caller owns disposal.</param>
    /// <param name="cancellationToken">Cancels connection acceptance and both frame operations.</param>
    private static async Task<BridgeRequest> ServeListOnceAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await pipe.WaitForConnectionAsync(cancellationToken);
        var requestJson = await FrameCodec.ReadAsync(pipe, cancellationToken);
        var request = JsonSerializer.Deserialize<BridgeRequest>(requestJson!)!;
        var response = FakePipeServer.Success(request, new ListSessionsResponse
        {
            TotalMatched = 2,
            Sessions =
            [
                new() { Id = 23, Method = "GET", Url = "http://127.0.0.1/stdio-wire-test", Host = "127.0.0.1",
                    StatusCode = 200, IsComplete = true, ResponseBodyBytes = 3_000_000_012 }
            ]
        });
        await FrameCodec.WriteAsync(pipe, JsonSerializer.Serialize(response), cancellationToken);
        return request;
    }

    /// <summary>Terminates only this child and checks remaining stdout, even when an assertion or read fails.</summary>
    /// <param name="process">The test-owned child. The caller disposes its handle.</param>
    /// <param name="stderr">The discard-only drain task, never included in diagnostics.</param>
    private static async Task StopHostAsync(Process process, Task stderr)
    {
        // Cleanup has its own short deadline because the operation deadline may already have expired.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(cleanup.Token);
            while (await process.StandardOutput.ReadLineAsync(cleanup.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
            }
        }
        finally
        {
            try
            {
                await stderr.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException)
            {
                // The child is terminated. A cancelled discard-only drain has no evidence to retain.
            }
        }
    }
}
