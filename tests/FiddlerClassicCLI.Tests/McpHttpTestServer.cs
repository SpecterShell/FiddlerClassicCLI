// Hosts isolated loopback MCP wire tests with in-memory credentials and bounded request lifetimes.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FiddlerClassicCLI.Host.Mcp;

namespace FiddlerClassicCLI.Tests;

internal sealed class McpHttpTestServer : IAsyncDisposable
{
    internal const string ProtocolVersion = "2026-07-28";
    private const string TestToken = "http-protocol-test-only";
    private readonly ManagedHttpServer _server;
    private readonly CancellationTokenSource _timeout;
    internal HttpClient Client { get; }
    internal CancellationToken Token => _timeout.Token;

    private McpHttpTestServer(ManagedHttpServer server, CancellationTokenSource timeout, int port)
    {
        _server = server;
        _timeout = timeout;
        Client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp")
        };
    }

    /// <summary>Starts the production transport without reading or writing user configuration.</summary>
    /// <returns>A fixture that owns the listener, HTTP client, and 15-second deadline.</returns>
    internal static async Task<McpHttpTestServer> StartAsync()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var server = await McpHost.StartHttpAsync(IPAddress.Loopback, port,
                new SingleTokenCredentialProvider(TestToken), new HttpConnectionRegistry(), timeout.Token);
            return new McpHttpTestServer(server, timeout, port);
        }
        catch
        {
            timeout.Dispose();
            throw;
        }
    }

    /// <summary>Builds a modern request, leaving headers mutable for negative wire tests.</summary>
    /// <param name="method">The JSON-RPC method and mirrored method header.</param>
    /// <param name="tool">Optional tool name and mirrored name header.</param>
    /// <param name="version">The exact metadata and HTTP-header protocol version.</param>
    /// <returns>A request owned by the caller.</returns>
    internal static HttpRequestMessage Request(string method, string? tool = null, string version = ProtocolVersion)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["_meta"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = version,
                ["io.modelcontextprotocol/clientCapabilities"] = new { },
                ["io.modelcontextprotocol/clientInfo"] = new { name = "http-wire-tests", version = "1.0" }
            }
        };
        if (tool is not null)
        {
            parameters["name"] = tool;
            parameters["arguments"] = new { pageSize = 1 };
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method,
                @params = parameters
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", version);
        request.Headers.Add("Mcp-Method", method);
        if (tool is not null)
        {
            request.Headers.Add("Mcp-Name", tool);
        }

        return request;
    }

    /// <summary>Reads either a JSON response or the final JSON-RPC result in a request-scoped SSE stream.</summary>
    /// <param name="response">The response, retained by the caller.</param>
    /// <param name="cancellationToken">Bounds response reading.</param>
    /// <returns>A detached JSON value containing the final response.</returns>
    internal static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var payload = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? text.Split('\n').Last(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]
            : text;
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
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
}
