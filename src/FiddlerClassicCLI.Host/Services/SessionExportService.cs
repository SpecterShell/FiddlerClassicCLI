// Reproduces captured sessions as cURL, raw HTTP, or HAR through the bounded bridge APIs.
using System.Text;
using System.Text.Json;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal sealed class SessionExportService
{
    private readonly IBridgeClient _bridgeClient;

    /// <summary>
    /// Creates an exporter over the shared bridge client.
    /// </summary>
    /// <param name="bridgeClient">Reads captured metadata, headers, and bounded body chunks.</param>
    public SessionExportService(IBridgeClient bridgeClient)
    {
        _bridgeClient = bridgeClient;
    }

    /// <summary>
    /// Exports one request as a shell cURL command or byte-exact raw HTTP request.
    /// </summary>
    /// <param name="sessionId">The captured session ID.</param>
    /// <param name="format">The <c>curl</c> or <c>raw-http</c> output format.</param>
    /// <param name="outputPath">The destination path or <c>-</c> for stdout.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <param name="cancellationToken">Cancels bridge reads and output writes.</param>
    public async Task<ExportResult> ExportSingleAsync(
        int sessionId,
        string format,
        string outputPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var details = await GetDetails(sessionId, cancellationToken).ConfigureAwait(false);
        await using var output = OpenOutput(outputPath, overwrite, out var resolvedPath);
        if (string.Equals(format, "curl", StringComparison.OrdinalIgnoreCase))
        {
            var body = await ReadBodyAsync(sessionId, BodyDirections.Request, cancellationToken).ConfigureAwait(false);
            var command = BuildCurl(details, body);
            var bytes = new UTF8Encoding(false).GetBytes(command + Environment.NewLine);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(format, "raw-http", StringComparison.OrdinalIgnoreCase))
        {
            await WriteRawHttpAsync(details, output, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException("Format must be 'curl' or 'raw-http'.", nameof(format));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new ExportResult { Path = resolvedPath, Format = format, SessionCount = 1 };
    }

    /// <summary>
    /// Writes a HAR 1.2 document for a bounded filtered session set, retaining at most one session's bodies at a time.
    /// </summary>
    /// <param name="filters">The filters and maximum session count to export.</param>
    /// <param name="outputPath">The absolute HAR destination path.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <param name="cancellationToken">Cancels bridge reads and output writes.</param>
    public async Task<ExportResult> ExportHarAsync(
        ListSessionsRequest filters,
        string outputPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(outputPath) || !string.Equals(Path.GetExtension(outputPath), ".har", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("HAR output must be an absolute path ending in .har.", nameof(outputPath));
        }

        var sessions = await _bridgeClient.SendAsync<ListSessionsRequest, ListSessionsResponse>(
            Operations.ListSessions,
            filters,
            cancellationToken).ConfigureAwait(false);
        await using var output = OpenOutput(outputPath, overwrite, out var resolvedPath);
        await WriteUtf8(output, "{\"log\":{\"version\":\"1.2\",\"creator\":{\"name\":\"fiddler-classic\",\"version\":\"0.3\"},\"entries\":[", cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < sessions.Sessions.Count; index++)
        {
            if (index > 0)
            {
                await WriteUtf8(output, ",", cancellationToken).ConfigureAwait(false);
            }

            var summary = sessions.Sessions[index];
            var details = await GetDetails(summary.Id, cancellationToken).ConfigureAwait(false);
            var requestBody = await ReadBodyAsync(summary.Id, BodyDirections.Request, cancellationToken).ConfigureAwait(false);
            var responseBody = await ReadBodyAsync(summary.Id, BodyDirections.Response, cancellationToken).ConfigureAwait(false);
            var entry = CreateHarEntry(details, requestBody, responseBody);
            await JsonSerializer.SerializeAsync(output, entry, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await WriteUtf8(output, "]}}", cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new ExportResult
        {
            Path = resolvedPath,
            Format = "har",
            SessionCount = sessions.Sessions.Count
        };
    }

    private async Task<SessionDetails> GetDetails(int sessionId, CancellationToken cancellationToken)
    {
        return await _bridgeClient.SendAsync<GetSessionDetailsRequest, SessionDetails>(
            Operations.GetSessionDetails,
            new GetSessionDetailsRequest { SessionId = sessionId, IncludeHeaders = true },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadBodyAsync(int sessionId, string direction, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await CopyBodyAsync(sessionId, direction, output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    /// <summary>
    /// Copies a complete captured body from bounded bridge chunks to an output stream.
    /// </summary>
    /// <param name="sessionId">The captured session ID.</param>
    /// <param name="direction">The request or response body direction.</param>
    /// <param name="output">The destination stream.</param>
    /// <param name="cancellationToken">Cancels reads and writes.</param>
    private async Task CopyBodyAsync(
        int sessionId,
        string direction,
        Stream output,
        CancellationToken cancellationToken)
    {
        long offset = 0;
        while (true)
        {
            var chunk = await _bridgeClient.SendAsync<GetSessionBodyRequest, SessionBodyChunk>(
                Operations.GetSessionBody,
                new GetSessionBodyRequest
                {
                    SessionId = sessionId,
                    Direction = direction,
                    Offset = offset,
                    Length = ProtocolConstants.CliBodyChunkBytes
                },
                cancellationToken).ConfigureAwait(false);
            var bytes = Convert.FromBase64String(chunk.Base64Data);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            offset += bytes.Length;
            if (chunk.EndOfBody)
            {
                return;
            }

            if (bytes.Length == 0)
            {
                throw new InvalidDataException("The Fiddler bridge returned an empty body chunk before EOF.");
            }
        }
    }

    private async Task WriteRawHttpAsync(SessionDetails details, Stream output, CancellationToken cancellationToken)
    {
        var summary = details.Summary;
        var startLine = $"{summary.Method} {summary.PathAndQuery} {summary.Protocol ?? "HTTP/1.1"}\r\n";
        await WriteUtf8(output, startLine, cancellationToken).ConfigureAwait(false);
        foreach (var header in details.RequestHeaders ?? new List<HeaderDto>())
        {
            await WriteUtf8(output, $"{header.Name}: {header.Value}\r\n", cancellationToken).ConfigureAwait(false);
        }

        await WriteUtf8(output, "\r\n", cancellationToken).ConfigureAwait(false);
        await CopyBodyAsync(summary.Id, BodyDirections.Request, output, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildCurl(SessionDetails details, byte[] body)
    {
        string? bodyText = null;
        if (body.Length > 0)
        {
            try
            {
                bodyText = new UTF8Encoding(false, true).GetString(body);
                if (bodyText.IndexOf('\0') >= 0)
                {
                    throw new DecoderFallbackException();
                }
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("Binary request bodies cannot be represented as one cURL command. Use --format raw-http.");
            }
        }

        var parts = new List<string> { "curl", "--request", ShellQuote(details.Summary.Method) };
        foreach (var header in details.RequestHeaders ?? new List<HeaderDto>())
        {
            parts.Add("--header");
            parts.Add(ShellQuote($"{header.Name}: {header.Value}"));
        }

        if (bodyText != null)
        {
            parts.Add("--data-binary");
            parts.Add(ShellQuote(bodyText));
        }

        parts.Add(ShellQuote(details.Summary.Url));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Projects one captured transaction into a HAR 1.2 entry with base64-preserved body bytes.
    /// </summary>
    /// <param name="details">The session metadata and exact headers.</param>
    /// <param name="requestBody">The complete raw request body.</param>
    /// <param name="responseBody">The complete raw response body.</param>
    private static object CreateHarEntry(SessionDetails details, byte[] requestBody, byte[] responseBody)
    {
        var summary = details.Summary;
        var uri = Uri.TryCreate(summary.Url, UriKind.Absolute, out var parsedUri) ? parsedUri : null;
        var query = uri == null
            ? Array.Empty<object>()
            : uri.Query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split(new[] { '=' }, 2))
                .Select(pair => (object)new
                {
                    name = Uri.UnescapeDataString(pair[0]),
                    value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty
                }).ToArray();
        return new
        {
            startedDateTime = summary.StartedAtUtc ?? DateTime.UnixEpoch.ToString("O"),
            time = summary.DurationMilliseconds ?? 0,
            request = new
            {
                method = summary.Method,
                url = summary.Url,
                httpVersion = summary.Protocol ?? "HTTP/1.1",
                headers = ToHarHeaders(details.RequestHeaders),
                queryString = query,
                postData = requestBody.Length == 0 ? null : new
                {
                    mimeType = HeaderValue(details.RequestHeaders, "Content-Type") ?? "application/octet-stream",
                    text = Convert.ToBase64String(requestBody),
                    encoding = "base64"
                },
                headersSize = -1,
                bodySize = requestBody.LongLength
            },
            response = new
            {
                status = summary.StatusCode ?? 0,
                statusText = summary.StatusDescription ?? string.Empty,
                httpVersion = summary.Protocol ?? "HTTP/1.1",
                headers = ToHarHeaders(details.ResponseHeaders),
                content = new
                {
                    size = responseBody.LongLength,
                    mimeType = summary.ContentType ?? "application/octet-stream",
                    text = Convert.ToBase64String(responseBody),
                    encoding = "base64"
                },
                redirectURL = HeaderValue(details.ResponseHeaders, "Location") ?? string.Empty,
                headersSize = -1,
                bodySize = responseBody.LongLength
            },
            cache = new { },
            timings = new { send = 0, wait = summary.DurationMilliseconds ?? 0, receive = 0 }
        };
    }

    private static object[] ToHarHeaders(IEnumerable<HeaderDto>? headers)
    {
        return (headers ?? Array.Empty<HeaderDto>())
            .Select(header => (object)new { name = header.Name, value = header.Value })
            .ToArray();
    }

    private static string? HeaderValue(IEnumerable<HeaderDto>? headers, string name)
    {
        return headers?.FirstOrDefault(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    private static Stream OpenOutput(string outputPath, bool overwrite, out string resolvedPath)
    {
        if (outputPath == "-")
        {
            resolvedPath = "-";
            return Console.OpenStandardOutput();
        }

        resolvedPath = Path.GetFullPath(outputPath);
        return new FileStream(
            resolvedPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            useAsync: true);
    }

    private static Task WriteUtf8(Stream output, string value, CancellationToken cancellationToken)
    {
        return output.WriteAsync(Encoding.UTF8.GetBytes(value), cancellationToken).AsTask();
    }
}

internal sealed class ExportResult
{
    public string Path { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public int SessionCount { get; set; }
}
