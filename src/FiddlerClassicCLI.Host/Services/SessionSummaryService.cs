// Aggregates one bounded session metadata page without fetching headers or payloads.
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal sealed class SessionSummaryService
{
    private readonly IBridgeClient _bridgeClient;

    /// <summary>
    /// Creates a metadata aggregator over the shared bridge client.
    /// </summary>
    /// <param name="bridgeClient">Retrieves a single bounded session-list snapshot.</param>
    public SessionSummaryService(IBridgeClient bridgeClient)
    {
        _bridgeClient = bridgeClient;
    }

    /// <summary>
    /// Aggregates only the returned page of one filtered list call, without following pagination.
    /// </summary>
    /// <param name="filters">Metadata filters, inclusive ID bounds, ordering, and a limit of 1 through 1000.</param>
    /// <param name="cancellationToken">Cancels the bridge call and subsequent aggregation.</param>
    /// <returns>Page-scoped counts, captured body-byte totals, and available completed timings.</returns>
    /// <exception cref="ArgumentException">Bounds are invalid or header/body search was requested.</exception>
    public async Task<SessionSummaryResult> SummarizeAsync(
        ListSessionsRequest filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ValidateFilters(filters);
        // Save scope before awaiting. Do not derive it from the live capture after the list call.
        var limit = filters.Limit;
        var newestFirst = filters.NewestFirst;
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _bridgeClient.SendAsync<ListSessionsRequest, ListSessionsResponse>(
            Operations.ListSessions, filters, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (response.Sessions is null || response.Sessions.Count > limit
            || response.TotalMatched < response.Sessions.Count
            || response.Sessions.Any(session => session is null
                || session.RequestBodyBytes < 0 || session.ResponseBodyBytes < 0))
        {
            throw new BridgeClientException(ErrorCodes.InvalidRequest, "The Fiddler bridge returned invalid session-list metadata.");
        }

        var sessions = response.Sessions;
        var timings = sessions
            .Where(session => session.IsComplete && session.DurationMilliseconds.HasValue)
            .Select(session => session.DurationMilliseconds!.Value)
            .Where(duration => double.IsFinite(duration) && duration >= 0)
            .Order()
            .ToArray();

        return new SessionSummaryResult
        {
            Limit = limit,
            NewestFirst = newestFirst,
            TotalMatched = response.TotalMatched,
            Returned = sessions.Count,
            MinReturnedId = sessions.Count == 0 ? null : sessions.Min(session => session.Id),
            MaxReturnedId = sessions.Count == 0 ? null : sessions.Max(session => session.Id),
            CompletedCount = sessions.Count(session => session.IsComplete),
            RequestBodyBytes = sessions.Sum(session => session.RequestBodyBytes),
            ResponseBodyBytes = sessions.Sum(session => session.ResponseBodyBytes),
            ByHost = sessions.GroupBy(session => session.Host, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SessionHostCount { Host = group.Key, Count = group.Count() }).ToList(),
            ByStatus = sessions.GroupBy(session => session.StatusCode)
                .OrderBy(group => group.Key)
                .Select(group => new SessionStatusCount { StatusCode = group.Key, Count = group.Count() }).ToList(),
            DurationMilliseconds = SummarizeTimings(timings)
        };
    }

    /// <summary>
    /// Rejects invalid metadata bounds and payload-search filters before contacting the bridge.
    /// </summary>
    /// <param name="filters">The existing list contract supplied by either CLI or MCP.</param>
    private static void ValidateFilters(ListSessionsRequest filters)
    {
        if (filters.Limit < 1 || filters.Limit > ProtocolConstants.MaxSessionLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(filters), $"Limit must be between 1 and {ProtocolConstants.MaxSessionLimit}.");
        }

        if (filters.MinId < 0 || filters.MaxId < 0 || filters.MinId > filters.MaxId)
        {
            throw new ArgumentException("Session ID bounds are invalid.", nameof(filters));
        }

        if (filters.MinDurationMilliseconds is double min && (!double.IsFinite(min) || min < 0)
            || filters.MaxDurationMilliseconds is double max && (!double.IsFinite(max) || max < 0)
            || filters.MinDurationMilliseconds > filters.MaxDurationMilliseconds)
        {
            throw new ArgumentException("Duration bounds must be finite, nonnegative, and ordered.", nameof(filters));
        }

        if (filters.MinBodyBytes < 0 || filters.MaxBodyBytes < 0 || filters.MinBodyBytes > filters.MaxBodyBytes)
        {
            throw new ArgumentException("Body-size bounds are invalid.", nameof(filters));
        }

        if (!string.IsNullOrEmpty(filters.HeaderName) || !string.IsNullOrEmpty(filters.HeaderValue)
            || !string.IsNullOrEmpty(filters.BodyContains))
        {
            throw new ArgumentException("Session summaries support metadata filters only. Header and body searches are not supported.", nameof(filters));
        }
    }

    /// <summary>
    /// Computes a middle-value median and nearest-rank p95 over finite, nonnegative sorted timings.
    /// </summary>
    /// <param name="sorted">Ascending completed-session durations in milliseconds, at most the list limit.</param>
    /// <returns>Null statistics for an empty sample. Zero remains a valid measured duration.</returns>
    private static SessionTimingDistribution SummarizeTimings(double[] sorted)
    {
        if (sorted.Length == 0)
        {
            return new SessionTimingDistribution();
        }

        var middle = sorted.Length / 2;
        return new SessionTimingDistribution
        {
            Count = sorted.Length,
            Min = sorted[0],
            Max = sorted[^1],
            Median = sorted.Length % 2 == 0
                ? sorted[middle - 1] + (sorted[middle] - sorted[middle - 1]) / 2
                : sorted[middle],
            P95 = sorted[(95 * sorted.Length + 99) / 100 - 1]
        };
    }
}
