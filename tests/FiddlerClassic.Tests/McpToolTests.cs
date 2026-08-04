// Verifies the MCP tool surface, annotations, pagination, and body encoding behavior.
using System.Reflection;
using System.Text;
using FiddlerClassic.Host.Mcp;
using FiddlerClassic.Host.Services;
using FiddlerClassic.Protocol;
using ModelContextProtocol.Server;

namespace FiddlerClassic.Tests;

public sealed class McpToolTests
{
    /// <summary>
    /// Verifies the exact public tool set and its read-only, destructive, open-world, and structured annotations.
    /// </summary>
    [Fact]
    public void ExposesApprovedToolSetAndSafetyAnnotations()
    {
        var tools = typeof(FiddlerTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(item => item.Attribute is not null)
            .ToDictionary(item => item.Attribute!.Name!, item => item.Attribute!);

        var expected = new[]
        {
            "get_status", "start_capture", "stop_capture", "list_network_requests", "wait_for_network_request",
            "get_network_request", "get_network_request_body", "clear_network_requests", "remove_network_requests",
            "save_network_archive", "load_network_archive", "replay_network_request", "send_request",
            "diff_network_requests", "list_websocket_messages", "get_websocket_message"
        };
        Assert.Equal(expected.Order(), tools.Keys.Order());
        Assert.True(tools["get_status"].ReadOnly);
        Assert.True(tools["clear_network_requests"].Destructive);
        Assert.True(tools["remove_network_requests"].Destructive);
        Assert.True(tools["replay_network_request"].OpenWorld);
        Assert.True(tools["send_request"].OpenWorld);
        Assert.All(tools.Values, attribute => Assert.True(attribute.UseStructuredContent));
    }

    [Fact]
    public async Task SessionListForwardsRichFilters()
    {
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.ListSessions, operation);
                var filters = Assert.IsType<ListSessionsRequest>(request);
                Assert.Equal("Authorization", filters.HeaderName);
                Assert.Equal(250, filters.MinDurationMilliseconds);
                Assert.Equal("HTTP/2", filters.Protocol);
                Assert.True(filters.IsError);
                Assert.Equal("needle", filters.BodyContains);
                Assert.Equal(4096, filters.BodySearchBytes);
                return new ListSessionsResponse();
            }
        };

        await CreateTools(bridge).ListNetworkRequests(
            headerName: "Authorization",
            minDurationMs: 250,
            protocol: "HTTP/2",
            isError: true,
            bodyContains: "needle",
            bodySearchBytes: 4096,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitToolReturnsExplicitTimeoutState()
    {
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.WaitForSession, operation);
                Assert.Equal(42, Assert.IsType<WaitForSessionRequest>(request).AfterId);
                return new WaitForSessionResponse { Matched = false, LatestSessionId = 45 };
            }
        };

        var result = await CreateTools(bridge).WaitForNetworkRequest(
            afterRequestId: 42,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Matched);
        Assert.Equal(45, result.LatestRequestId);
        Assert.Null(result.Request);
    }

    [Fact]
    public async Task SendRequestCanWaitForCorrelatedCapture()
    {
        var calls = 0;
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                calls++;
                if (operation == Operations.SendRequest)
                {
                    return new QueuedResponse
                    {
                        Accepted = true,
                        BaselineSessionId = 10,
                        ExpectedMethod = "GET",
                        ExpectedUrl = "https://example.test/"
                    };
                }

                Assert.Equal(Operations.WaitForSession, operation);
                var wait = Assert.IsType<WaitForSessionRequest>(request);
                Assert.Equal(10, wait.AfterId);
                Assert.Equal("https://example.test/", wait.Filters.UrlContains);
                return new WaitForSessionResponse
                {
                    Matched = true,
                    Session = new SessionSummary { Id = 11, Url = "https://example.test/" }
                };
            }
        };

        var result = await CreateTools(bridge).SendRequest(
            "https://example.test/",
            wait: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.Equal(11, result.CapturedRequest?.RequestId);
    }

    [Fact]
    public async Task WebSocketTextFrameReturnsTextMetadata()
    {
        var bytes = Encoding.UTF8.GetBytes("hello");
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.GetWebSocketMessage, operation);
                return new WebSocketMessageChunk
                {
                    SessionId = 7,
                    MessageId = 3,
                    Opcode = "text",
                    Direction = WebSocketDirections.Received,
                    BytesReturned = bytes.Length,
                    TotalBytes = bytes.Length,
                    EndOfMessage = true,
                    Base64Data = Convert.ToBase64String(bytes)
                };
            }
        };

        var result = await CreateTools(bridge).GetWebSocketMessage(
            7,
            3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("text", result.Encoding);
        Assert.Equal("hello", result.Data);
    }

    /// <summary>
    /// Verifies valid UTF-8 bytes for a textual media type are returned with text metadata.
    /// </summary>
    [Fact]
    public async Task BodyToolReturnsTextMetadataForTextContent()
    {
        var bridge = BodyBridge("text/plain; charset=utf-8", Encoding.UTF8.GetBytes("hello"));
        var tools = CreateTools(bridge);

        var result = await tools.GetNetworkRequestBody(
            7,
            BodyDirections.Response,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("text", result.Encoding);
        Assert.Equal("hello", result.Data);
        Assert.True(result.Eof);
        Assert.Equal(5, result.TotalBytes);
    }

    /// <summary>
    /// Verifies binary media types preserve body bytes through base64 output.
    /// </summary>
    [Fact]
    public async Task BodyToolReturnsBase64ForBinaryContent()
    {
        var bytes = new byte[] { 0, 1, 2, 255 };
        var bridge = BodyBridge("application/octet-stream", bytes);
        var tools = CreateTools(bridge);

        var result = await tools.GetNetworkRequestBody(
            7,
            BodyDirections.Request,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("base64", result.Encoding);
        Assert.Equal(Convert.ToBase64String(bytes), result.Data);
    }

    /// <summary>
    /// Verifies invalid UTF-8 is never replaced or otherwise lossily decoded.
    /// </summary>
    [Fact]
    public async Task BodyToolDoesNotLossilyDecodeInvalidUtf8()
    {
        var bytes = new byte[] { 0xC3, 0x28 };
        var tools = CreateTools(BodyBridge("text/plain; charset=utf-8", bytes));

        var result = await tools.GetNetworkRequestBody(
            7,
            BodyDirections.Response,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("base64", result.Encoding);
        Assert.Equal(Convert.ToBase64String(bytes), result.Data);
    }

    [Fact]
    public async Task BodyToolEnforcesMcpChunkLimitBeforeCallingBridge()
    {
        var tools = CreateTools(new TestBridgeClient());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tools.GetNetworkRequestBody(
            1,
            BodyDirections.Response,
            length: ProtocolConstants.McpMaxBodyChunkBytes + 1,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies continuation bounds advance past the returned edge ID in each ordering direction.
    /// </summary>
    /// <param name="newestFirst">Whether the page is ordered by descending IDs.</param>
    /// <param name="expectedNextMaxRequestId">The expected continuation bound for descending order.</param>
    /// <param name="expectedNextMinRequestId">The expected continuation bound for ascending order.</param>
    [Theory]
    [InlineData(true, 7, null)]
    [InlineData(false, null, 13)]
    public async Task SessionListReturnsStableIdContinuation(
        bool newestFirst,
        int? expectedNextMaxRequestId,
        int? expectedNextMinRequestId)
    {
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.ListSessions, operation);
                var listRequest = Assert.IsType<ListSessionsRequest>(request);
                Assert.Equal(newestFirst, listRequest.NewestFirst);
                Assert.Equal(2, listRequest.Limit);
                return new ListSessionsResponse
                {
                    TotalMatched = 5,
                    Sessions = new List<SessionSummary>
                    {
                        new() { Id = 12 },
                        new() { Id = 8 }
                    }
                };
            }
        };

        var result = await CreateTools(bridge).ListNetworkRequests(
            pageSize: 2,
            newestFirst: newestFirst,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, result.TotalMatched);
        Assert.Equal(2, result.Returned);
        Assert.True(result.HasMore);
        Assert.Equal(expectedNextMaxRequestId, result.NextMaxRequestId);
        Assert.Equal(expectedNextMinRequestId, result.NextMinRequestId);
        Assert.Equal(new[] { 12, 8 }, result.Requests.Select(request => request.RequestId));
    }

    /// <summary>
    /// Verifies invalid page sizes and inverted ID bounds are rejected before bridge access.
    /// </summary>
    /// <param name="limit">The invalid requested page size.</param>
    /// <param name="minId">The optional lower request-ID bound.</param>
    /// <param name="maxId">The optional upper request-ID bound.</param>
    [Theory]
    [InlineData(0, null, null)]
    [InlineData(1001, null, null)]
    [InlineData(100, 20, 10)]
    public async Task SessionListRejectsInvalidPaginationBeforeCallingBridge(int limit, int? minId, int? maxId)
    {
        var bridge = new TestBridgeClient
        {
            Handler = (_, _) => throw new Xunit.Sdk.XunitException("Bridge should not be called.")
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => CreateTools(bridge).ListNetworkRequests(
            minRequestId: minId,
            maxRequestId: maxId,
            pageSize: limit,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies invalid directions and offsets are rejected before bridge access.
    /// </summary>
    /// <param name="direction">The candidate body direction.</param>
    /// <param name="offset">The candidate body offset.</param>
    [Theory]
    [InlineData("sideways", 0)]
    [InlineData("request", -1)]
    public async Task BodyToolRejectsInvalidRangesBeforeCallingBridge(string direction, long offset)
    {
        var bridge = new TestBridgeClient
        {
            Handler = (_, _) => throw new Xunit.Sdk.XunitException("Bridge should not be called.")
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => CreateTools(bridge).GetNetworkRequestBody(
            1,
            direction,
            offset,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Creates a bridge test double that returns one complete body chunk with controlled metadata.
    /// </summary>
    /// <param name="contentType">The captured response media type.</param>
    /// <param name="bytes">The exact body bytes to return.</param>
    private static TestBridgeClient BodyBridge(string contentType, byte[] bytes)
    {
        return new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.GetSessionBody, operation);
                var bodyRequest = Assert.IsType<GetSessionBodyRequest>(request);
                return new SessionBodyChunk
                {
                    SessionId = bodyRequest.SessionId,
                    Direction = bodyRequest.Direction,
                    Offset = bodyRequest.Offset,
                    BytesReturned = bytes.Length,
                    TotalBytes = bytes.Length,
                    EndOfBody = true,
                    Base64Data = Convert.ToBase64String(bytes),
                    ContentType = contentType
                };
            }
        };
    }

    private static FiddlerTools CreateTools(TestBridgeClient bridge)
    {
        var environment = new FiddlerEnvironment();
        return new FiddlerTools(bridge, new StatusService(bridge, environment));
    }
}
