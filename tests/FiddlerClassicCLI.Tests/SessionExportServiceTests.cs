// Verifies sensitive session reproduction and HAR generation over bounded bridge calls.
using System.Text;
using System.Text.Json;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class SessionExportServiceTests
{
    [Fact]
    public async Task RawHttpExportPreservesStartLineHeadersAndBinaryBody()
    {
        var body = new byte[] { 0, 1, 2, 255 };
        var bridge = ExportBridge(body, Array.Empty<byte>());
        var path = Path.Combine(Path.GetTempPath(), $"fiddler-raw-{Guid.NewGuid():N}.http");

        try
        {
            await new SessionExportService(bridge).ExportSingleAsync(
                7,
                "raw-http",
                path,
                overwrite: false,
                TestContext.Current.CancellationToken);

            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            var prefix = Encoding.UTF8.GetBytes("POST /submit?q=1 HTTP/1.1\r\nContent-Type: application/octet-stream\r\n\r\n");
            Assert.Equal(prefix.Concat(body), bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HarExportIncludesBase64RequestAndResponseBodies()
    {
        var requestBody = Encoding.UTF8.GetBytes("request");
        var responseBody = new byte[] { 0, 255 };
        var bridge = ExportBridge(requestBody, responseBody, includeList: true);
        var path = Path.Combine(Path.GetTempPath(), $"fiddler-har-{Guid.NewGuid():N}.har");

        try
        {
            var result = await new SessionExportService(bridge).ExportHarAsync(
                new ListSessionsRequest { Limit = 1 },
                path,
                overwrite: false,
                TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var entry = document.RootElement.GetProperty("log").GetProperty("entries")[0];
            Assert.Equal(1, result.SessionCount);
            Assert.Equal(Convert.ToBase64String(requestBody), entry.GetProperty("request").GetProperty("postData").GetProperty("text").GetString());
            Assert.Equal(Convert.ToBase64String(responseBody), entry.GetProperty("response").GetProperty("content").GetProperty("text").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Creates a bridge double that serves one session's details and complete request and response body chunks.
    /// </summary>
    /// <param name="requestBody">The raw request bytes.</param>
    /// <param name="responseBody">The raw response bytes.</param>
    /// <param name="includeList">Whether the bridge also serves the HAR list operation.</param>
    private static TestBridgeClient ExportBridge(byte[] requestBody, byte[] responseBody, bool includeList = false)
    {
        var summary = new SessionSummary
        {
            Id = 7,
            Method = "POST",
            Url = "https://example.test/submit?q=1",
            PathAndQuery = "/submit?q=1",
            Protocol = "HTTP/1.1",
            StatusCode = 200,
            StatusDescription = "OK",
            ContentType = "application/octet-stream",
            RequestBodyBytes = requestBody.Length,
            ResponseBodyBytes = responseBody.Length
        };
        return new TestBridgeClient
        {
            Handler = (operation, request) => operation switch
            {
                Operations.ListSessions when includeList => new ListSessionsResponse
                {
                    TotalMatched = 1,
                    Sessions = new List<SessionSummary> { summary }
                },
                Operations.GetSessionDetails => new SessionDetails
                {
                    Summary = summary,
                    RequestHeaders = new List<HeaderDto>
                    {
                        new() { Name = "Content-Type", Value = "application/octet-stream" }
                    },
                    ResponseHeaders = new List<HeaderDto>
                    {
                        new() { Name = "Content-Type", Value = "application/octet-stream" }
                    }
                },
                Operations.GetSessionBody => BodyChunk(Assert.IsType<GetSessionBodyRequest>(request), requestBody, responseBody),
                _ => throw new InvalidOperationException($"Unexpected operation {operation}.")
            }
        };
    }

    private static SessionBodyChunk BodyChunk(GetSessionBodyRequest request, byte[] requestBody, byte[] responseBody)
    {
        var body = request.Direction == BodyDirections.Request ? requestBody : responseBody;
        return new SessionBodyChunk
        {
            SessionId = request.SessionId,
            Direction = request.Direction,
            Offset = request.Offset,
            BytesReturned = body.Length,
            TotalBytes = body.Length,
            EndOfBody = true,
            Base64Data = Convert.ToBase64String(body),
            ContentType = "application/octet-stream"
        };
    }
}
