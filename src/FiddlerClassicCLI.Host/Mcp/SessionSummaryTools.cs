// Exposes metadata-only aggregation of one bounded network-request page through MCP.
using System.ComponentModel;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;
using ModelContextProtocol.Server;

namespace FiddlerClassicCLI.Host.Mcp;

[McpServerToolType]
internal sealed class SessionSummaryTools
{
    private readonly SessionSummaryService _summaryService;

    /// <summary>
    /// Creates the summary tool using the same host service as CLI summary output.
    /// </summary>
    /// <param name="bridgeClient">Retrieves bounded captured metadata without sending network traffic.</param>
    public SessionSummaryTools(IBridgeClient bridgeClient)
    {
        _summaryService = new SessionSummaryService(bridgeClient);
    }

    /// <summary>
    /// Summarizes a single filtered metadata page without reading headers, bodies, or later pages.
    /// </summary>
    /// <param name="minRequestId">The inclusive minimum request ID.</param>
    /// <param name="maxRequestId">The inclusive maximum request ID.</param>
    /// <param name="method">An exact HTTP method filter.</param>
    /// <param name="host">A case-insensitive host substring.</param>
    /// <param name="urlContains">A case-insensitive full URL substring.</param>
    /// <param name="statusCode">An exact HTTP status code.</param>
    /// <param name="contentType">A case-insensitive response content-type substring.</param>
    /// <param name="process">A case-insensitive client process substring.</param>
    /// <param name="minDurationMs">The minimum completed duration in milliseconds.</param>
    /// <param name="maxDurationMs">The maximum completed duration in milliseconds.</param>
    /// <param name="protocol">An exact HTTP protocol token.</param>
    /// <param name="minBodyBytes">The minimum combined captured request and response body length.</param>
    /// <param name="maxBodyBytes">The maximum combined captured request and response body length.</param>
    /// <param name="isError">The optional aborted or HTTP 400+ state.</param>
    /// <param name="limit">The maximum number of matching requests included in the aggregates.</param>
    /// <param name="newestFirst">Whether to select requests in descending ID order.</param>
    /// <param name="cancellationToken">Cancels the list call and aggregation.</param>
    /// <returns>Returned-page scope, truncation, host/status counts, body-byte totals, and completed timings.</returns>
    [McpServerTool(Name = "summarize_network_requests", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Summarize one bounded page of captured network-request metadata. Reports scope, limit, truncation, host/status counts, captured body bytes, and available completed timings in milliseconds with median and nearest-rank p95. Wire byte counts are unavailable. Performs no header or body reads or searches, pagination, or outgoing network requests. Totals cover only returned requests. Filters may match more.")]
    public Task<SessionSummaryResult> SummarizeNetworkRequests(
        [Description("Minimum request ID, inclusive.")] int? minRequestId = null,
        [Description("Maximum request ID, inclusive.")] int? maxRequestId = null,
        [Description("Exact HTTP method filter.")] string? method = null,
        [Description("Case-insensitive host substring filter.")] string? host = null,
        [Description("Case-insensitive full URL substring filter.")] string? urlContains = null,
        [Description("Exact HTTP status code filter.")] int? statusCode = null,
        [Description("Case-insensitive response content-type substring filter.")] string? contentType = null,
        [Description("Case-insensitive client process substring filter.")] string? process = null,
        [Description("Minimum completed duration in milliseconds, finite and nonnegative.")] double? minDurationMs = null,
        [Description("Maximum completed duration in milliseconds, finite and nonnegative.")] double? maxDurationMs = null,
        [Description("Exact HTTP protocol, such as HTTP/1.1.")] string? protocol = null,
        [Description("Minimum combined captured request and response body bytes.")] long? minBodyBytes = null,
        [Description("Maximum combined captured request and response body bytes.")] long? maxBodyBytes = null,
        [Description("Filter errors (aborted or status 400+) when true, successful traffic when false.")] bool? isError = null,
        [Description("Maximum requests to aggregate, from 1 through 1000. Default 100. Does not bound the bridge's metadata filter scan.")] int limit = ProtocolConstants.DefaultSessionLimit,
        [Description("Select newest requests first. False selects oldest first.")] bool newestFirst = true,
        CancellationToken cancellationToken = default)
    {
        return _summaryService.SummarizeAsync(new ListSessionsRequest
        {
            MinId = minRequestId,
            MaxId = maxRequestId,
            Method = method,
            Host = host,
            UrlContains = urlContains,
            StatusCode = statusCode,
            ContentType = contentType,
            Process = process,
            MinDurationMilliseconds = minDurationMs,
            MaxDurationMilliseconds = maxDurationMs,
            Protocol = protocol,
            MinBodyBytes = minBodyBytes,
            MaxBodyBytes = maxBodyBytes,
            IsError = isError,
            Limit = limit,
            NewestFirst = newestFirst
        }, cancellationToken);
    }
}
