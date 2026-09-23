// Verifies summary MCP annotations, generated schema, and metadata filter forwarding without transports.
using System.Reflection;
using System.Text.Json;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Protocol;
using ModelContextProtocol.Server;

namespace FiddlerClassicCLI.Tests;

public sealed class SessionSummaryToolsTests
{
    [Fact]
    public void ExposesOneReadOnlyClosedWorldIdempotentStructuredTool()
    {
        Assert.NotNull(typeof(SessionSummaryTools).GetCustomAttribute<McpServerToolTypeAttribute>());
        var method = Assert.Single(typeof(SessionSummaryTools).GetMethods(),
            method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal("summarize_network_requests", attribute.Name);
        Assert.True(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.True(attribute.UseStructuredContent);

        var tool = McpServerTool.Create(method, new SessionSummaryTools(new TestBridgeClient())).ProtocolTool;
        Assert.Equal(attribute.Name, tool.Name);
        Assert.True(tool.Annotations!.ReadOnlyHint);
        Assert.False(tool.Annotations.DestructiveHint);
        Assert.True(tool.Annotations.IdempotentHint);
        Assert.False(tool.Annotations.OpenWorldHint);
        var properties = tool.InputSchema.GetProperty("properties");
        Assert.Equal(new[]
        {
            "minRequestId", "maxRequestId", "method", "host", "urlContains", "statusCode", "contentType",
            "process", "minDurationMs", "maxDurationMs", "protocol", "minBodyBytes", "maxBodyBytes",
            "isError", "limit", "newestFirst"
        }.Order(), properties.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(ProtocolConstants.DefaultSessionLimit, properties.GetProperty("limit").GetProperty("default").GetInt32());
        Assert.NotNull(tool.OutputSchema);
    }

    [Fact]
    public async Task ForwardsAllMetadataFiltersWithoutHeaderOrBodySearch()
    {
        var calls = 0;
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                calls++;
                Assert.Equal(Operations.ListSessions, operation);
                var filters = Assert.IsType<ListSessionsRequest>(request);
                Assert.Equal(10, filters.MinId);
                Assert.Equal(20, filters.MaxId);
                Assert.Equal("POST", filters.Method);
                Assert.Equal("example.test", filters.Host);
                Assert.Equal("/submit", filters.UrlContains);
                Assert.Equal(500, filters.StatusCode);
                Assert.Equal("application/json", filters.ContentType);
                Assert.Equal("client", filters.Process);
                Assert.Equal(1, filters.MinDurationMilliseconds);
                Assert.Equal(50, filters.MaxDurationMilliseconds);
                Assert.Equal("HTTP/1.1", filters.Protocol);
                Assert.Equal(10, filters.MinBodyBytes);
                Assert.Equal(100, filters.MaxBodyBytes);
                Assert.True(filters.IsError);
                Assert.Equal(2, filters.Limit);
                Assert.False(filters.NewestFirst);
                Assert.Null(filters.HeaderName);
                Assert.Null(filters.HeaderValue);
                Assert.Null(filters.BodyContains);
                return new ListSessionsResponse
                {
                    TotalMatched = 3,
                    Sessions = [new() { Id = 11, Host = "example.test", StatusCode = 500, ResponseBodyBytes = 40 }]
                };
            }
        };

        var result = await new SessionSummaryTools(bridge).SummarizeNetworkRequests(
            minRequestId: 10, maxRequestId: 20, method: "POST", host: "example.test", urlContains: "/submit",
            statusCode: 500, contentType: "application/json", process: "client", minDurationMs: 1,
            maxDurationMs: 50, protocol: "HTTP/1.1", minBodyBytes: 10, maxBodyBytes: 100,
            isError: true, limit: 2, newestFirst: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Equal("returned_sessions", result.Scope);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.Limit);
        Assert.Equal(1, result.Returned);
        Assert.Equal(3, result.TotalMatched);
        Assert.Equal(40, result.ResponseBodyBytes);
        Assert.Equal(1, Assert.Single(result.ByHost).Count);
        Assert.Equal(500, Assert.Single(result.ByStatus).StatusCode);

        var json = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);
        Assert.Equal("returned_sessions", json.GetProperty("scope").GetString());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(40, json.GetProperty("responseBodyBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("durationMilliseconds").GetProperty("median").ValueKind);
        Assert.False(json.TryGetProperty("sessions", out _));
        Assert.False(json.TryGetProperty("headers", out _));
        Assert.False(json.TryGetProperty("body", out _));
    }

    [Fact]
    public async Task UsesBoundedDefaultsForEmptyCapture()
    {
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.ListSessions, operation);
                var filters = Assert.IsType<ListSessionsRequest>(request);
                Assert.Equal(ProtocolConstants.DefaultSessionLimit, filters.Limit);
                Assert.True(filters.NewestFirst);
                return new ListSessionsResponse();
            }
        };

        var result = await new SessionSummaryTools(bridge).SummarizeNetworkRequests(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Returned);
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// Ensures MCP and CLI share pre-bridge limit validation in the host service.
    /// </summary>
    /// <param name="limit">An invalid aggregate limit.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task RejectsInvalidLimitBeforeBridgeCall(int limit)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SessionSummaryTools(new TestBridgeClient())
            .SummarizeNetworkRequests(limit: limit, cancellationToken: TestContext.Current.CancellationToken));
    }
}
