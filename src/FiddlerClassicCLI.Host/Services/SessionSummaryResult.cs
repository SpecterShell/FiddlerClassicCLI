// Defines host-only aggregates whose counts, bytes, and timings cover the returned page only.
using System.ComponentModel;

namespace FiddlerClassicCLI.Host.Services;

internal sealed class SessionSummaryResult
{
    [Description("All aggregates are limited to returned_sessions. Other matched sessions and the rest of the capture are excluded.")]
    public string Scope => "returned_sessions";
    public int Limit { get; set; }
    public bool NewestFirst { get; set; }
    [Description("All sessions matching the supplied filters at the time of the single list snapshot.")]
    public int TotalMatched { get; set; }
    public int Returned { get; set; }
    [Description("True when matching sessions were omitted from the aggregate because of the result limit.")]
    public bool Truncated => TotalMatched > Returned;
    public int? MinReturnedId { get; set; }
    public int? MaxReturnedId { get; set; }
    public int CompletedCount { get; set; }
    [Description("Sum of captured request body lengths in the returned page, including incomplete sessions. Excludes headers and wire overhead.")]
    public long RequestBodyBytes { get; set; }
    [Description("Sum of captured response body lengths in the returned page, including incomplete sessions. Excludes headers and wire overhead.")]
    public long ResponseBodyBytes { get; set; }
    [Description("Case-insensitive host counts, sorted by host. Empty host metadata forms its own group.")]
    public List<SessionHostCount> ByHost { get; set; } = new();
    [Description("Counts by exact status code, sorted ascending. Null groups sessions without a captured status.")]
    public List<SessionStatusCount> ByStatus { get; set; } = new();
    [Description("Finite nonnegative durations for completed returned sessions only, from ClientBeginRequest through ClientDoneResponse.")]
    public SessionTimingDistribution DurationMilliseconds { get; set; } = new();
}

internal sealed class SessionHostCount
{
    public string Host { get; set; } = string.Empty;
    public int Count { get; set; }
}

internal sealed class SessionStatusCount
{
    public int? StatusCode { get; set; }
    public int Count { get; set; }
}

internal sealed class SessionTimingDistribution
{
    public int Count { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    [Description("Middle value, or the average of the two middle values for an even sample. Null when count is zero.")]
    public double? Median { get; set; }
    [Description("Nearest-rank 95th percentile: sorted value at ceil(0.95 * count). Null when count is zero.")]
    public double? P95 { get; set; }
}
