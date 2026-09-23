// Exposes bounded, safety-annotated Fiddler operations as MCP tools.
using System.ComponentModel;
using System.Text;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;
using ModelContextProtocol.Server;

namespace FiddlerClassicCLI.Host.Mcp;

[McpServerToolType]
internal sealed class FiddlerTools
{
    private readonly IBridgeClient _bridgeClient;
    private readonly StatusService _statusService;

    /// <summary>
    /// Creates the MCP tool set from the shared bridge and status services.
    /// </summary>
    /// <param name="bridgeClient">Sends operational requests to Fiddler.</param>
    /// <param name="statusService">Provides offline-capable status information.</param>
    public FiddlerTools(IBridgeClient bridgeClient, StatusService statusService)
    {
        _bridgeClient = bridgeClient;
        _statusService = statusService;
    }

    [McpServerTool(Name = "get_status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Get Fiddler Classic installation, process, bridge, proxy, and capture status. Works while Fiddler is offline.")]
    public async Task<McpStatus> GetStatus(CancellationToken cancellationToken)
    {
        var status = await _statusService.GetAsync(cancellationToken).ConfigureAwait(false);
        return McpStatus.Create(status);
    }

    [McpServerTool(Name = "start_capture", ReadOnly = false, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description(CaptureGuidance.Start)]
    public Task<CaptureResponse> StartCapture(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<SetCaptureRequest, CaptureResponse>(
            Operations.SetCapture,
            new SetCaptureRequest { Enabled = true },
            cancellationToken);
    }

    [McpServerTool(Name = "stop_capture", ReadOnly = false, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description(CaptureGuidance.Stop)]
    public Task<CaptureResponse> StopCapture(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<SetCaptureRequest, CaptureResponse>(
            Operations.SetCapture,
            new SetCaptureRequest { Enabled = false },
            cancellationToken);
    }

    /// <summary>
    /// Validates request-oriented filters and projects a stable ID-bound page from Fiddler sessions.
    /// </summary>
    /// <param name="minRequestId">The inclusive minimum request ID.</param>
    /// <param name="maxRequestId">The inclusive maximum request ID.</param>
    /// <param name="method">An exact HTTP method filter.</param>
    /// <param name="host">A case-insensitive host substring.</param>
    /// <param name="urlContains">A case-insensitive full URL substring.</param>
    /// <param name="statusCode">An exact HTTP status code.</param>
    /// <param name="contentType">A case-insensitive response content-type substring.</param>
    /// <param name="process">A case-insensitive client process substring.</param>
    /// <param name="headerName">An exact case-insensitive request or response header name.</param>
    /// <param name="headerValue">A case-insensitive request or response header value substring.</param>
    /// <param name="minDurationMs">The minimum completed duration in milliseconds.</param>
    /// <param name="maxDurationMs">The maximum completed duration in milliseconds.</param>
    /// <param name="protocol">An exact HTTP protocol token.</param>
    /// <param name="minBodyBytes">The minimum combined request and response body length.</param>
    /// <param name="maxBodyBytes">The maximum combined request and response body length.</param>
    /// <param name="isError">The optional aborted or HTTP 400+ state.</param>
    /// <param name="bodyContains">The exact UTF-8 byte sequence to find in a body prefix.</param>
    /// <param name="bodyDirection">The request or response body searched.</param>
    /// <param name="bodySearchBytes">The bounded body prefix length searched per request.</param>
    /// <param name="pageSize">The bounded number of results to return.</param>
    /// <param name="newestFirst">Whether results should be ordered by descending request ID.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "list_network_requests", ReadOnly = true, UseStructuredContent = true)]
    [Description("List captured network requests and obtain request IDs. Returns metadata and stable ID-bound continuation fields. Headers and bodies are never included.")]
    public async Task<McpNetworkRequestPage> ListNetworkRequests(
        [Description("Minimum request ID, inclusive.")] int? minRequestId = null,
        [Description("Maximum request ID, inclusive.")] int? maxRequestId = null,
        [Description("Exact HTTP method filter.")] string? method = null,
        [Description("Case-insensitive host substring filter.")] string? host = null,
        [Description("Case-insensitive full URL substring filter.")] string? urlContains = null,
        [Description("Exact HTTP status code filter.")] int? statusCode = null,
        [Description("Case-insensitive response content-type substring filter.")] string? contentType = null,
        [Description("Case-insensitive client process substring filter.")] string? process = null,
        [Description("Exact case-insensitive request or response header name.")] string? headerName = null,
        [Description("Case-insensitive request or response header value substring.")] string? headerValue = null,
        [Description("Minimum completed request duration in milliseconds.")] double? minDurationMs = null,
        [Description("Maximum completed request duration in milliseconds.")] double? maxDurationMs = null,
        [Description("Exact HTTP protocol, such as HTTP/1.1.")] string? protocol = null,
        [Description("Minimum combined request and response body bytes.")] long? minBodyBytes = null,
        [Description("Maximum combined request and response body bytes.")] long? maxBodyBytes = null,
        [Description("Filter errors (aborted or status 400+) when true, successful traffic when false.")] bool? isError = null,
        [Description("Exact UTF-8 byte sequence to find in a bounded request or response body prefix.")] string? bodyContains = null,
        [Description("Body direction used by bodyContains: request or response.")] string bodyDirection = BodyDirections.Response,
        [Description("Maximum body prefix bytes searched per request, from 1 through 1048576.")] int bodySearchBytes = ProtocolConstants.DefaultBodySearchBytes,
        [Description("Maximum requests to return, from 1 to 1000.")] int pageSize = ProtocolConstants.DefaultSessionLimit,
        [Description("Return newest requests first.")] bool newestFirst = true,
        CancellationToken cancellationToken = default)
    {
        if (pageSize < 1 || pageSize > ProtocolConstants.MaxSessionLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"pageSize must be between 1 and {ProtocolConstants.MaxSessionLimit}.");
        }

        if (minRequestId.HasValue && maxRequestId.HasValue && minRequestId.Value > maxRequestId.Value)
        {
            throw new ArgumentException("minRequestId cannot be greater than maxRequestId.");
        }

        var response = await _bridgeClient.SendAsync<ListSessionsRequest, ListSessionsResponse>(
            Operations.ListSessions,
            new ListSessionsRequest
            {
                MinId = minRequestId,
                MaxId = maxRequestId,
                Method = method,
                Host = host,
                UrlContains = urlContains,
                StatusCode = statusCode,
                ContentType = contentType,
                Process = process,
                HeaderName = headerName,
                HeaderValue = headerValue,
                MinDurationMilliseconds = minDurationMs,
                MaxDurationMilliseconds = maxDurationMs,
                Protocol = protocol,
                MinBodyBytes = minBodyBytes,
                MaxBodyBytes = maxBodyBytes,
                IsError = isError,
                BodyContains = bodyContains,
                BodyDirection = bodyDirection,
                BodySearchBytes = bodySearchBytes,
                Limit = pageSize,
                NewestFirst = newestFirst
            },
            cancellationToken).ConfigureAwait(false);

        return McpNetworkRequestPage.Create(response, newestFirst);
    }

    /// <summary>
    /// Waits for the first completed request after an exclusive ID cursor and returns timeout state explicitly.
    /// </summary>
    /// <param name="afterRequestId">The exclusive request ID cursor.</param>
    /// <param name="timeoutMs">The bounded wait duration in milliseconds.</param>
    /// <param name="method">An optional exact method filter.</param>
    /// <param name="host">An optional host substring.</param>
    /// <param name="urlContains">An optional URL substring.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "wait_for_network_request", ReadOnly = true, UseStructuredContent = true)]
    [Description("Wait for the first completed network request after an exclusive request ID. Returns matched=false on timeout and never includes headers or bodies.")]
    public async Task<McpWaitForNetworkRequestResult> WaitForNetworkRequest(
        [Description("Exclusive request ID cursor. Use 0 to include the next completed request.")] int afterRequestId = 0,
        [Description("Wait duration in milliseconds, from 1 through 60000.")] int timeoutMs = ProtocolConstants.DefaultWaitMilliseconds,
        [Description("Exact HTTP method filter.")] string? method = null,
        [Description("Case-insensitive host substring filter.")] string? host = null,
        [Description("Case-insensitive full URL substring filter.")] string? urlContains = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _bridgeClient.SendAsync<WaitForSessionRequest, WaitForSessionResponse>(
            Operations.WaitForSession,
            new WaitForSessionRequest
            {
                AfterId = afterRequestId,
                TimeoutMilliseconds = timeoutMs,
                Filters = new ListSessionsRequest
                {
                    Method = method,
                    Host = host,
                    UrlContains = urlContains
                }
            },
            cancellationToken).ConfigureAwait(false);
        return McpWaitForNetworkRequestResult.Create(response);
    }

    /// <summary>
    /// Retrieves one request's metadata and includes exact headers only when explicitly requested.
    /// </summary>
    /// <param name="requestId">The captured request ID.</param>
    /// <param name="includeHeaders">Whether exact request and response headers should be returned.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "get_network_request", ReadOnly = true, UseStructuredContent = true)]
    [Description("Get metadata for a request ID returned by list_network_requests. Raw headers are optional. Use get_network_request_body for payload bytes.")]
    public async Task<McpNetworkRequestDetails> GetNetworkRequest(
        [Description("Request ID returned by list_network_requests.")] int requestId,
        [Description("Include exact request and response headers.")] bool includeHeaders = false,
        CancellationToken cancellationToken = default)
    {
        var details = await _bridgeClient.SendAsync<GetSessionDetailsRequest, SessionDetails>(
            Operations.GetSessionDetails,
            new GetSessionDetailsRequest { SessionId = requestId, IncludeHeaders = includeHeaders },
            cancellationToken).ConfigureAwait(false);
        return McpNetworkRequestDetails.Create(details);
    }

    /// <summary>
    /// Reads one bounded body range and returns lossless UTF-8 text only when content metadata and bytes permit it.
    /// </summary>
    /// <param name="requestId">The captured request ID.</param>
    /// <param name="direction">Whether to read the request or response body.</param>
    /// <param name="offset">The zero-based byte offset.</param>
    /// <param name="length">The bounded number of bytes to request.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "get_network_request_body", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read up to 64 KiB from a captured request or response body. Continue with offset + bytesReturned until eof=true. Output may contain credentials, cookies, or tokens.")]
    public async Task<McpNetworkBodyChunk> GetNetworkRequestBody(
        [Description("Request ID returned by list_network_requests.")] int requestId,
        [Description("Body direction: request or response.")] string direction,
        [Description("Zero-based byte offset.")] long offset = 0,
        [Description("Bytes to read, from 1 through 65536.")] int length = ProtocolConstants.McpMaxBodyChunkBytes,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(direction, BodyDirections.Request, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(direction, BodyDirections.Response, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Direction must be 'request' or 'response'.", nameof(direction));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        }

        if (length < 1 || length > ProtocolConstants.McpMaxBodyChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"Length must be between 1 and {ProtocolConstants.McpMaxBodyChunkBytes}.");
        }

        var chunk = await _bridgeClient.SendAsync<GetSessionBodyRequest, SessionBodyChunk>(
            Operations.GetSessionBody,
            new GetSessionBodyRequest
            {
                SessionId = requestId,
                Direction = direction,
                Offset = offset,
                Length = length
            },
            cancellationToken).ConfigureAwait(false);

        var bytes = Convert.FromBase64String(chunk.Base64Data);
        var isText = TryDecodeUtf8(chunk.ContentType, bytes, out var text);
        return new McpNetworkBodyChunk
        {
            RequestId = chunk.SessionId,
            Direction = chunk.Direction,
            Offset = chunk.Offset,
            BytesReturned = chunk.BytesReturned,
            TotalBytes = chunk.TotalBytes,
            Eof = chunk.EndOfBody,
            ContentType = chunk.ContentType,
            Encoding = isText ? "text" : "base64",
            Data = isText ? text : chunk.Base64Data
        };
    }

    [McpServerTool(Name = "clear_network_requests", ReadOnly = false, Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Permanently remove all captured network requests from Fiddler. Requires confirm=true.")]
    public Task<ClearSessionsResponse> ClearNetworkRequests(
        [Description("Must be true to confirm destructive clearing.")] bool confirm,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<ClearSessionsRequest, ClearSessionsResponse>(
            Operations.ClearSessions,
            new ClearSessionsRequest { Confirm = confirm },
            cancellationToken);
    }

    /// <summary>
    /// Removes an exact set of captured requests after explicit destructive confirmation.
    /// </summary>
    /// <param name="requestIds">The complete bounded request ID set.</param>
    /// <param name="confirm">Whether the caller explicitly confirmed deletion.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "remove_network_requests", ReadOnly = false, Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Permanently remove only the specified captured network requests. Requires confirm=true and fails if any ID is missing.")]
    public Task<RemoveSessionsResponse> RemoveNetworkRequests(
        [Description("One to 1000 request IDs returned by list_network_requests.")] int[] requestIds,
        [Description("Must be true to confirm destructive removal.")] bool confirm,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<RemoveSessionsRequest, RemoveSessionsResponse>(
            Operations.RemoveSessions,
            new RemoveSessionsRequest { SessionIds = requestIds, Confirm = confirm },
            cancellationToken);
    }

    /// <summary>
    /// Maps request IDs and explicit overwrite controls into a SAZ export operation.
    /// </summary>
    /// <param name="path">The absolute destination SAZ path.</param>
    /// <param name="requestIds">The optional captured request IDs to export.</param>
    /// <param name="overwrite">Whether an existing archive may be replaced.</param>
    /// <param name="confirmOverwrite">Whether replacement has been explicitly confirmed.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "save_network_archive", ReadOnly = false, Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description("Save captured network requests to an absolute .saz archive path. Existing files require overwrite=true and confirmOverwrite=true.")]
    public async Task<McpNetworkArchiveResult> SaveNetworkArchive(
        [Description("Absolute destination path ending in .saz.")] string path,
        [Description("Optional request IDs from list_network_requests. Omit to save all requests.")] int[]? requestIds = null,
        [Description("Allow replacement of an existing archive.")] bool overwrite = false,
        [Description("Explicitly confirm replacement of an existing archive.")] bool confirmOverwrite = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _bridgeClient.SendAsync<SaveSessionsRequest, ArchiveResponse>(
            Operations.SaveSessions,
            new SaveSessionsRequest
            {
                Path = path,
                SessionIds = requestIds,
                Overwrite = overwrite,
                ConfirmOverwrite = confirmOverwrite
            },
            cancellationToken).ConfigureAwait(false);
        return McpNetworkArchiveResult.Create(result);
    }

    /// <summary>
    /// Imports captured requests from an existing absolute SAZ path.
    /// </summary>
    /// <param name="path">The absolute source SAZ path.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "load_network_archive", ReadOnly = false, Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description("Load captured network requests from an existing absolute .saz archive path into Fiddler.")]
    public async Task<McpNetworkArchiveResult> LoadNetworkArchive(
        [Description("Absolute source path ending in .saz.")] string path,
        CancellationToken cancellationToken = default)
    {
        var result = await _bridgeClient.SendAsync<LoadSessionsRequest, ArchiveResponse>(
            Operations.LoadSessions,
            new LoadSessionsRequest { Path = path },
            cancellationToken).ConfigureAwait(false);
        return McpNetworkArchiveResult.Create(result);
    }

    /// <summary>
    /// Queues one captured request for conditional or unconditional replay.
    /// </summary>
    /// <param name="requestId">The captured request ID.</param>
    /// <param name="unconditional">Whether conditional request headers should be omitted.</param>
    /// <param name="wait">Whether to wait for the resulting completed request.</param>
    /// <param name="timeoutMs">The bounded wait duration in milliseconds.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "replay_network_request", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Queue replay of a captured network request and optionally wait for the resulting completed transaction.")]
    public async Task<McpQueuedRequestResult> ReplayNetworkRequest(
        [Description("Request ID returned by list_network_requests.")] int requestId,
        [Description("Replay without conditional request headers.")] bool unconditional = false,
        [Description("Wait for the resulting completed network request.")] bool wait = false,
        [Description("Wait duration in milliseconds, from 1 through 60000.")] int timeoutMs = ProtocolConstants.DefaultWaitMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var queued = await _bridgeClient.SendAsync<ReplaySessionRequest, QueuedResponse>(
            Operations.ReplaySession,
            new ReplaySessionRequest { SessionId = requestId, Unconditional = unconditional },
            cancellationToken).ConfigureAwait(false);
        return await WaitForQueuedRequest(queued, wait, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enforces exclusive text or base64 bodies, validates request input, and queues the composed request.
    /// </summary>
    /// <param name="url">The absolute HTTP or HTTPS URL.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="headers">The optional ordered <c>Name: value</c> headers.</param>
    /// <param name="body">An optional UTF-8 text body.</param>
    /// <param name="bodyBase64">An optional base64-encoded body.</param>
    /// <param name="wait">Whether to wait for the resulting completed request.</param>
    /// <param name="timeoutMs">The bounded wait duration in milliseconds.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "send_request", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Queue an HTTP or HTTPS request through Fiddler and optionally wait for the resulting completed transaction.")]
    public async Task<McpQueuedRequestResult> SendRequest(
        [Description("Absolute http or https URL.")] string url,
        [Description("HTTP method.")] string method = "GET",
        [Description("Headers formatted as 'Name: value'. CR and LF are rejected.")] string[]? headers = null,
        [Description("Optional UTF-8 request body.")] string? body = null,
        [Description("Optional base64 request body. Cannot be combined with body.")] string? bodyBase64 = null,
        [Description("Wait for the resulting completed network request.")] bool wait = false,
        [Description("Wait duration in milliseconds, from 1 through 60000.")] int timeoutMs = ProtocolConstants.DefaultWaitMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (body is not null && bodyBase64 is not null)
        {
            throw new ArgumentException("body and bodyBase64 cannot be combined.");
        }

        var parsedHeaders = RequestInput.ParseHeaders(headers ?? Array.Empty<string>());
        var encodedBody = body is null ? bodyBase64 : Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        RequestInput.ValidateBody(encodedBody);

        var queued = await _bridgeClient.SendAsync<SendRequestRequest, QueuedResponse>(
            Operations.SendRequest,
            new SendRequestRequest
            {
                Url = url,
                Method = method,
                Headers = parsedHeaders,
                BodyBase64 = encodedBody
            },
            cancellationToken).ConfigureAwait(false);
        return await WaitForQueuedRequest(queued, wait, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "diff_network_requests", ReadOnly = true, UseStructuredContent = true)]
    [Description("Compare request metadata, exact ordered headers, timings, and SHA-256 body hashes without returning body content.")]
    public Task<SessionDiffResponse> DiffNetworkRequests(
        [Description("Left request ID.")] int leftRequestId,
        [Description("Right request ID.")] int rightRequestId,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<DiffSessionsRequest, SessionDiffResponse>(
            Operations.DiffSessions,
            new DiffSessionsRequest { LeftSessionId = leftRequestId, RightSessionId = rightRequestId },
            cancellationToken);
    }

    [McpServerTool(Name = "list_websocket_messages", ReadOnly = true, UseStructuredContent = true)]
    [Description("List WebSocket frame metadata for a captured upgraded request. Payload bytes are never included.")]
    public Task<ListWebSocketMessagesResponse> ListWebSocketMessages(
        [Description("Request ID whose session contains the WebSocket tunnel.")] int requestId,
        [Description("Zero-based message offset.")] int offset = 0,
        [Description("Maximum messages to return, from 1 through 1000.")] int limit = ProtocolConstants.DefaultSessionLimit,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<ListWebSocketMessagesRequest, ListWebSocketMessagesResponse>(
            Operations.ListWebSocketMessages,
            new ListWebSocketMessagesRequest { SessionId = requestId, Offset = offset, Limit = limit },
            cancellationToken);
    }

    /// <summary>
    /// Reads one bounded WebSocket payload range and emits lossless text only for complete valid UTF-8 bytes.
    /// </summary>
    /// <param name="requestId">The tunnel request ID.</param>
    /// <param name="messageId">The WebSocket message ID.</param>
    /// <param name="offset">The zero-based payload offset.</param>
    /// <param name="length">The bounded byte count.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "get_websocket_message", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read up to 64 KiB from one WebSocket frame payload with direction, opcode, total length, EOF, and text/base64 metadata.")]
    public async Task<McpWebSocketMessageChunk> GetWebSocketMessage(
        [Description("Request ID whose session contains the WebSocket tunnel.")] int requestId,
        [Description("Message ID returned by list_websocket_messages.")] int messageId,
        [Description("Zero-based payload offset.")] long offset = 0,
        [Description("Bytes to read, from 1 through 65536.")] int length = ProtocolConstants.McpMaxBodyChunkBytes,
        CancellationToken cancellationToken = default)
    {
        if (length < 1 || length > ProtocolConstants.McpMaxBodyChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var chunk = await _bridgeClient.SendAsync<GetWebSocketMessageRequest, WebSocketMessageChunk>(
            Operations.GetWebSocketMessage,
            new GetWebSocketMessageRequest
            {
                SessionId = requestId,
                MessageId = messageId,
                Offset = offset,
                Length = length
            },
            cancellationToken).ConfigureAwait(false);
        return McpWebSocketMessageChunk.Create(chunk);
    }

    /// <summary>
    /// Optionally waits after an accepted queue operation and projects timeout state without throwing.
    /// </summary>
    /// <param name="queued">The accepted queue operation and correlation hints.</param>
    /// <param name="wait">Whether the caller requested completion waiting.</param>
    /// <param name="timeoutMs">The bounded wait duration.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private async Task<McpQueuedRequestResult> WaitForQueuedRequest(
        QueuedResponse queued,
        bool wait,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (!wait)
        {
            return new McpQueuedRequestResult { Queued = queued };
        }

        var response = await _bridgeClient.SendAsync<WaitForSessionRequest, WaitForSessionResponse>(
            Operations.WaitForSession,
            new WaitForSessionRequest
            {
                AfterId = queued.BaselineSessionId,
                TimeoutMilliseconds = timeoutMs,
                Filters = new ListSessionsRequest
                {
                    Method = queued.ExpectedMethod,
                    UrlContains = queued.ExpectedUrl
                }
            },
            cancellationToken).ConfigureAwait(false);
        return new McpQueuedRequestResult
        {
            Queued = queued,
            TimedOut = !response.Matched,
            CapturedRequest = response.Session == null ? null : McpNetworkRequestSummary.Create(response.Session)
        };
    }

    /// <summary>
    /// Decodes bytes only for textual media types declared as UTF-8 and rejects invalid byte sequences.
    /// </summary>
    /// <param name="contentType">The captured Content-Type header.</param>
    /// <param name="bytes">The exact body chunk bytes.</param>
    /// <param name="text">Receives decoded text when the method succeeds.</param>
    private static bool TryDecodeUtf8(string? contentType, byte[] bytes, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var isText = contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        if (!isText)
        {
            return false;
        }

        var charsetIndex = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (charsetIndex >= 0)
        {
            var charset = contentType[(charsetIndex + "charset=".Length)..]
                .Split(';')[0]
                .Trim()
                .Trim('"');
            if (!string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(charset, "utf8", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
