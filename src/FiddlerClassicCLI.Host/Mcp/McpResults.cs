// Projects bridge response contracts into the compact public MCP result model.
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Mcp;

internal sealed class McpStatus
{
    public bool FiddlerInstalled { get; set; }
    public string? FiddlerPath { get; set; }
    public bool FiddlerRunning { get; set; }
    public int? FiddlerProcessId { get; set; }
    public string? FiddlerVersion { get; set; }
    public bool BridgeInstalled { get; set; }
    public bool BridgeConnected { get; set; }
    public string? BridgeVersion { get; set; }
    public bool IsProxyAttached { get; set; }
    public bool IsListening { get; set; }
    public int? ListenPort { get; set; }
    public bool IsHttpsDecryptionEnabled { get; set; }
    public int NetworkRequestCount { get; set; }
    public int CompletedNetworkRequestCount { get; set; }

    /// <summary>
    /// Projects internal bridge status into request-oriented MCP terminology.
    /// </summary>
    /// <param name="status">The bridge and environment status response.</param>
    public static McpStatus Create(StatusResponse status)
    {
        return new McpStatus
        {
            FiddlerInstalled = status.FiddlerInstalled,
            FiddlerPath = status.FiddlerPath,
            FiddlerRunning = status.FiddlerRunning,
            FiddlerProcessId = status.FiddlerProcessId,
            FiddlerVersion = status.FiddlerVersion,
            BridgeInstalled = status.BridgeInstalled,
            BridgeConnected = status.BridgeConnected,
            BridgeVersion = status.BridgeVersion,
            IsProxyAttached = status.IsProxyAttached,
            IsListening = status.IsListening,
            ListenPort = status.ListenPort,
            IsHttpsDecryptionEnabled = status.IsHttpsDecryptionEnabled,
            NetworkRequestCount = status.SessionCount,
            CompletedNetworkRequestCount = status.CompletedSessionCount
        };
    }
}

internal sealed class McpNetworkRequestPage
{
    public int TotalMatched { get; set; }
    public int Returned { get; set; }
    public bool HasMore { get; set; }
    public int? NextMinRequestId { get; set; }
    public int? NextMaxRequestId { get; set; }
    public List<McpNetworkRequestSummary> Requests { get; set; } = new();

    /// <summary>
    /// Creates a request page and derives the inclusive ID bound for the next stable page.
    /// </summary>
    /// <param name="response">The ordered bridge session-list response.</param>
    /// <param name="newestFirst">Whether decreasing IDs define the next page.</param>
    public static McpNetworkRequestPage Create(ListSessionsResponse response, bool newestFirst)
    {
        var page = new McpNetworkRequestPage
        {
            TotalMatched = response.TotalMatched,
            Returned = response.Sessions.Count,
            HasMore = response.TotalMatched > response.Sessions.Count,
            Requests = response.Sessions.Select(McpNetworkRequestSummary.Create).ToList()
        };

        if (!page.HasMore || page.Returned == 0)
        {
            return page;
        }

        if (newestFirst)
        {
            var lowestId = page.Requests.Min(request => request.RequestId);
            page.NextMaxRequestId = lowestId > int.MinValue ? lowestId - 1 : null;
        }
        else
        {
            var highestId = page.Requests.Max(request => request.RequestId);
            page.NextMinRequestId = highestId < int.MaxValue ? highestId + 1 : null;
        }

        return page;
    }
}

internal sealed class McpNetworkRequestDetails
{
    public McpNetworkRequestSummary Request { get; set; } = new();
    public List<HeaderDto>? RequestHeaders { get; set; }
    public List<HeaderDto>? ResponseHeaders { get; set; }

    public static McpNetworkRequestDetails Create(SessionDetails details)
    {
        return new McpNetworkRequestDetails
        {
            Request = McpNetworkRequestSummary.Create(details.Summary),
            RequestHeaders = details.RequestHeaders,
            ResponseHeaders = details.ResponseHeaders
        };
    }
}

internal sealed class McpNetworkRequestSummary
{
    public int RequestId { get; set; }
    public string State { get; set; } = string.Empty;
    public bool IsComplete { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string PathAndQuery { get; set; } = string.Empty;
    public string Scheme { get; set; } = string.Empty;
    public string? Protocol { get; set; }
    public int? StatusCode { get; set; }
    public string? StatusDescription { get; set; }
    public string? ContentType { get; set; }
    public string? Process { get; set; }
    public int ProcessId { get; set; }
    public string? ClientIp { get; set; }
    public string? ServerIp { get; set; }
    public long RequestBodyBytes { get; set; }
    public long ResponseBodyBytes { get; set; }
    public string? StartedAtUtc { get; set; }
    public double? DurationMilliseconds { get; set; }
    public bool IsError { get; set; }
    public bool HasWebSocketMessages { get; set; }

    /// <summary>
    /// Renames a bridge session summary into the public MCP network-request model.
    /// </summary>
    /// <param name="session">The bridge session metadata.</param>
    public static McpNetworkRequestSummary Create(SessionSummary session)
    {
        return new McpNetworkRequestSummary
        {
            RequestId = session.Id,
            State = session.State,
            IsComplete = session.IsComplete,
            Method = session.Method,
            Url = session.Url,
            Host = session.Host,
            PathAndQuery = session.PathAndQuery,
            Scheme = session.Scheme,
            Protocol = session.Protocol,
            StatusCode = session.StatusCode,
            StatusDescription = session.StatusDescription,
            ContentType = session.ContentType,
            Process = session.Process,
            ProcessId = session.ProcessId,
            ClientIp = session.ClientIp,
            ServerIp = session.ServerIp,
            RequestBodyBytes = session.RequestBodyBytes,
            ResponseBodyBytes = session.ResponseBodyBytes,
            StartedAtUtc = session.StartedAtUtc,
            DurationMilliseconds = session.DurationMilliseconds,
            IsError = session.IsError,
            HasWebSocketMessages = session.HasWebSocketMessages
        };
    }
}

internal sealed class McpNetworkBodyChunk
{
    public int RequestId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public long Offset { get; set; }
    public int BytesReturned { get; set; }
    public long TotalBytes { get; set; }
    public bool Eof { get; set; }
    public string? ContentType { get; set; }
    public string Encoding { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

internal sealed class McpNetworkArchiveResult
{
    public string Path { get; set; } = string.Empty;
    public int RequestCount { get; set; }

    public static McpNetworkArchiveResult Create(ArchiveResponse response)
    {
        return new McpNetworkArchiveResult
        {
            Path = response.Path,
            RequestCount = response.SessionCount
        };
    }
}

internal sealed class McpWaitForNetworkRequestResult
{
    public bool Matched { get; set; }
    public int LatestRequestId { get; set; }
    public McpNetworkRequestSummary? Request { get; set; }

    public static McpWaitForNetworkRequestResult Create(WaitForSessionResponse response)
    {
        return new McpWaitForNetworkRequestResult
        {
            Matched = response.Matched,
            LatestRequestId = response.LatestSessionId,
            Request = response.Session == null ? null : McpNetworkRequestSummary.Create(response.Session)
        };
    }
}

internal sealed class McpQueuedRequestResult
{
    public QueuedResponse Queued { get; set; } = new();
    public bool TimedOut { get; set; }
    public McpNetworkRequestSummary? CapturedRequest { get; set; }
}

internal sealed class McpWebSocketMessageChunk
{
    public int RequestId { get; set; }
    public int MessageId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Opcode { get; set; } = string.Empty;
    public bool IsFinal { get; set; }
    public long Offset { get; set; }
    public int BytesReturned { get; set; }
    public long TotalBytes { get; set; }
    public bool Eof { get; set; }
    public string Encoding { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public string? TimestampUtc { get; set; }

    /// <summary>
    /// Projects a protocol payload chunk and emits text only for a complete valid UTF-8 byte range.
    /// </summary>
    /// <param name="chunk">The exact WebSocket payload bytes and frame metadata.</param>
    public static McpWebSocketMessageChunk Create(WebSocketMessageChunk chunk)
    {
        var bytes = Convert.FromBase64String(chunk.Base64Data);
        var isTextFrame = string.Equals(chunk.Opcode, "text", StringComparison.OrdinalIgnoreCase);
        var text = string.Empty;
        var isUtf8 = isTextFrame && TryDecodeUtf8(bytes, out text);
        return new McpWebSocketMessageChunk
        {
            RequestId = chunk.SessionId,
            MessageId = chunk.MessageId,
            Direction = chunk.Direction,
            Opcode = chunk.Opcode,
            IsFinal = chunk.IsFinal,
            Offset = chunk.Offset,
            BytesReturned = chunk.BytesReturned,
            TotalBytes = chunk.TotalBytes,
            Eof = chunk.EndOfMessage,
            Encoding = isUtf8 ? "text" : "base64",
            Data = isUtf8 ? text : chunk.Base64Data,
            TimestampUtc = chunk.TimestampUtc
        };
    }

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }
}
