// Exercises bearer authentication on the loopback Streamable HTTP MCP transport.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using FiddlerClassic.Host.Mcp;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Tests;

public sealed class HttpTransportTests
{
    /// <summary>
    /// Verifies missing and invalid tokens are rejected while an authenticated tool call reaches the bridge.
    /// </summary>
    [Fact]
    public async Task RequiresValidBearerTokenOnLoopbackMcpEndpoint()
    {
        var port = GetFreePort();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var serverTask = McpHost.RunHttpAsync(port, "test-secret", shutdown.Token);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        await WaitForServer(client, TestContext.Current.CancellationToken);

        using var missing = await SendInitialize(client, null, TestContext.Current.CancellationToken);
        using var invalid = await SendInitialize(client, "wrong", TestContext.Current.CancellationToken);
        using var valid = await SendInitialize(client, "test-secret", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        var pipeTask = FakePipeServer.ServeOnceAsync(request => FakePipeServer.Success(
            request,
            new ListSessionsResponse
            {
                TotalMatched = 1,
                Sessions = new List<SessionSummary> { new() { Id = 31, Method = "GET", Url = "https://example.test/http" } }
            }));
        using var toolCall = await SendToolCall(client, TestContext.Current.CancellationToken);
        var toolPayload = await toolCall.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var bridgeRequest = await pipeTask;

        Assert.Equal(HttpStatusCode.OK, toolCall.StatusCode);
        Assert.Equal(Operations.ListSessions, bridgeRequest.Operation);
        Assert.Contains("example.test/http", toolPayload);

        shutdown.Cancel();
        await serverTask;
    }

    /// <summary>
    /// Sends an MCP initialize request with an optional bearer token.
    /// </summary>
    /// <param name="client">The loopback HTTP client.</param>
    /// <param name="token">The optional bearer token.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    private static async Task<HttpResponseMessage> SendInitialize(
        HttpClient client,
        string? token,
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
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends an authenticated request-list tool call over Streamable HTTP.
    /// </summary>
    /// <param name="client">The loopback HTTP client.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    private static async Task<HttpResponseMessage> SendToolCall(HttpClient client, CancellationToken cancellationToken)
    {
        const string payload = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_network_requests","arguments":{"pageSize":1}}}
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Polls the MCP endpoint until its authentication middleware responds.
    /// </summary>
    /// <param name="client">The loopback HTTP client.</param>
    /// <param name="cancellationToken">Cancels probes and polling delays.</param>
    private static async Task WaitForServer(HttpClient client, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var response = await client.GetAsync("/mcp", cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException("The MCP HTTP test server did not start.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
