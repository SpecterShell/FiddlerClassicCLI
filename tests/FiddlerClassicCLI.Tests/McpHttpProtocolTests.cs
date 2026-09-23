// Checks modern MCP HTTP wire contracts without SDK-client negotiation or a running Fiddler process.
using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class McpHttpProtocolTests
{
    [Fact]
    public async Task ListsToolsBeforeDiscoveryWithoutCreatingAProtocolSession()
    {
        await using var fixture = await McpHttpTestServer.StartAsync();
        using var list = McpHttpTestServer.Request("tools/list");
        list.Headers.Add("Mcp-Session-Id", "unused-legacy-id");
        using var listResponse = await fixture.Client.SendAsync(list, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.False(listResponse.Headers.Contains("Mcp-Session-Id"));
        var first = (await McpHttpTestServer.ReadResponseAsync(listResponse, fixture.Token)).GetProperty("result");
        AssertModernCacheResult(first);
        var tools = first.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(36, tools.Length);
        Assert.Equal(36, tools.Select(tool => tool.GetProperty("name").GetString()).Distinct().Count());
        var read = tools.Single(tool => tool.GetProperty("name").GetString() == "list_network_requests");
        Assert.True(read.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

        using var discover = McpHttpTestServer.Request("server/discover");
        using var discoveryResponse = await fixture.Client.SendAsync(discover, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, discoveryResponse.StatusCode);
        var discovery = (await McpHttpTestServer.ReadResponseAsync(discoveryResponse, fixture.Token)).GetProperty("result");
        AssertModernCacheResult(discovery);
        Assert.Contains(McpHttpTestServer.ProtocolVersion,
            discovery.GetProperty("supportedVersions").EnumerateArray().Select(version => version.GetString()));
        Assert.True(discovery.GetProperty("capabilities").TryGetProperty("tools", out _));

        using var repeat = McpHttpTestServer.Request("tools/list");
        using var repeatResponse = await fixture.Client.SendAsync(repeat, fixture.Token);
        var repeated = (await McpHttpTestServer.ReadResponseAsync(repeatResponse, fixture.Token)).GetProperty("result");
        Assert.Equal(first.GetProperty("tools").GetRawText(), repeated.GetProperty("tools").GetRawText());
    }

    /// <summary>Rejects missing or inconsistent modern headers before a tool can reach the bridge.</summary>
    /// <param name="header">The required header to remove or replace.</param>
    /// <param name="value">A mismatched value, or null to remove the header.</param>
    [Theory]
    [InlineData("MCP-Protocol-Version", null)]
    [InlineData("MCP-Protocol-Version", "2025-11-25")]
    [InlineData("Mcp-Method", null)]
    [InlineData("Mcp-Method", "tools/list")]
    [InlineData("Mcp-Name", null)]
    [InlineData("Mcp-Name", "remove_network_requests")]
    public async Task RejectsHeaderMismatch(string header, string? value)
    {
        await using var fixture = await McpHttpTestServer.StartAsync();
        using var request = McpHttpTestServer.Request("tools/call", "list_network_requests");
        request.Headers.Remove(header);
        if (value is not null)
        {
            request.Headers.Add(header, value);
        }

        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await McpHttpTestServer.ReadResponseAsync(response, fixture.Token);
        Assert.Equal(1, body.GetProperty("id").GetInt32());
        Assert.Equal(-32020, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task UnsupportedVersionReportsSupportedVersionsWithoutFallback()
    {
        await using var fixture = await McpHttpTestServer.StartAsync();
        using var request = McpHttpTestServer.Request("server/discover", version: "2099-01-01");
        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await McpHttpTestServer.ReadResponseAsync(response, fixture.Token);
        Assert.Equal(1, body.GetProperty("id").GetInt32());
        var error = body.GetProperty("error");
        Assert.Equal(-32022, error.GetProperty("code").GetInt32());
        Assert.Equal("2099-01-01", error.GetProperty("data").GetProperty("requested").GetString());
        Assert.Contains(McpHttpTestServer.ProtocolVersion,
            error.GetProperty("data").GetProperty("supported").EnumerateArray().Select(version => version.GetString()));
    }

    [Fact]
    public async Task UnknownMethodReturnsJsonRpcNotFound()
    {
        await using var fixture = await McpHttpTestServer.StartAsync();
        using var request = McpHttpTestServer.Request("tests/unknown");
        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await McpHttpTestServer.ReadResponseAsync(response, fixture.Token);
        Assert.Equal(-32601, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>Discovery and tools require bearer credentials on every request.</summary>
    /// <param name="method">The protocol method under test.</param>
    /// <param name="bearer">An absent or invalid test-only credential.</param>
    [Theory]
    [InlineData("server/discover", null)]
    [InlineData("server/discover", "invalid")]
    [InlineData("tools/list", null)]
    [InlineData("tools/list", "invalid")]
    public async Task ModernRequestsStillRequireAuthentication(string method, string? bearer)
    {
        await using var fixture = await McpHttpTestServer.StartAsync();
        using var request = McpHttpTestServer.Request(method);
        request.Headers.Authorization = bearer is null ? null : new("Bearer", bearer);
        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, challenge => challenge.Scheme == "Bearer");
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>No browser origins are authorized. Authentication does not override that restriction.</summary>
    /// <param name="origin">An untrusted, opaque, or malformed browser origin.</param>
    /// <param name="authenticated">Whether a valid credential accompanies the origin.</param>
    [Theory]
    [InlineData("https://untrusted.example", true)]
    [InlineData("https://untrusted.example", false)]
    [InlineData("null", true)]
    [InlineData("invalid-origin", true)]
    [InlineData("http://localhost", true)]
    [InlineData("", true)]
    public async Task RejectsBrowserOrigins(string origin, bool authenticated)
    {
        await using var fixture = await McpHttpTestServer.StartAsync();
        using var request = McpHttpTestServer.Request("server/discover");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        if (!authenticated)
        {
            request.Headers.Authorization = null;
        }
        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>The stateless endpoint has no standalone event stream or session-deletion route.</summary>
    /// <param name="method">A legacy session HTTP verb.</param>
    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task DoesNotExposeLegacySessionRoutes(string method)
    {
        await using var fixture = await McpHttpTestServer.StartAsync();
        using var request = McpHttpTestServer.Request("tools/list");
        request.Method = new HttpMethod(method);
        request.Content = null;
        using var response = await fixture.Client.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
    }

    [Fact]
    public async Task CancellingHttpRequestClosesThePendingBridgeExchange()
    {
        await using var fixture = await McpHttpTestServer.StartAsync();
        var pipeName = PipeNames.ForCurrentUser();
        Assert.StartsWith($"fiddler-classic-cli.tests.{Environment.ProcessId}.", pipeName, StringComparison.Ordinal);
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        using var request = McpHttpTestServer.Request("tools/call", "list_network_requests");
        var send = fixture.Client.SendAsync(request, cancellation.Token);
        try
        {
            await pipe.WaitForConnectionAsync(fixture.Token);
            var bridgeRequest = JsonSerializer.Deserialize<BridgeRequest>((await FrameCodec.ReadAsync(pipe, fixture.Token))!);
            Assert.Equal(Operations.ListSessions, bridgeRequest!.Operation);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
            // No synthetic response is sent: EOF proves cancellation reached the pipe client.
            Assert.Null(await FrameCodec.ReadAsync(pipe, fixture.Token));
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                using var response = await send;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    private static void AssertModernCacheResult(JsonElement result)
    {
        Assert.Equal("complete", result.GetProperty("resultType").GetString());
        Assert.Equal(0, result.GetProperty("ttlMs").GetInt64());
        Assert.Equal("private", result.GetProperty("cacheScope").GetString());
        var info = result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo");
        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("version").GetString()));
    }
}
