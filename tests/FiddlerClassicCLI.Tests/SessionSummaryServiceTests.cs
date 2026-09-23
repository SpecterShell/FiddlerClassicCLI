// Verifies bounded metadata aggregation using in-memory bridge clients only.
using System.Text.Json;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class SessionSummaryServiceTests
{
    [Fact]
    public async Task AggregatesOnlyOneReturnedPageAndPreservesFilterScope()
    {
        var filters = new ListSessionsRequest
        {
            MinId = 10,
            MaxId = 99,
            Host = "example.test",
            Limit = 4,
            NewestFirst = false
        };
        var response = new ListSessionsResponse
        {
            TotalMatched = 50,
            Sessions =
            [
                new() { Id = 10, Host = "B.example.test", StatusCode = 200, RequestBodyBytes = 3_000_000_000,
                    ResponseBodyBytes = 7, IsComplete = true, DurationMilliseconds = 20 },
                new() { Id = 12, Host = "b.EXAMPLE.test", StatusCode = 200, RequestBodyBytes = 4_000_000_000,
                    ResponseBodyBytes = 11, IsComplete = true, DurationMilliseconds = 10 },
                new() { Id = 15, Host = "a.example.test", StatusCode = 404, RequestBodyBytes = 13,
                    ResponseBodyBytes = 17, IsComplete = true },
                new() { Id = 19, RequestBodyBytes = 19, ResponseBodyBytes = 23, DurationMilliseconds = 999 }
            ]
        };
        var calls = 0;
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                calls++;
                Assert.Equal(Operations.ListSessions, operation);
                Assert.Same(filters, request);
                return response;
            }
        };

        var result = await new SessionSummaryService(bridge).SummarizeAsync(filters, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Equal("returned_sessions", result.Scope);
        Assert.Equal(50, result.TotalMatched);
        Assert.Equal(4, result.Returned);
        Assert.Equal(4, result.Limit);
        Assert.True(result.Truncated);
        Assert.False(result.NewestFirst);
        Assert.Equal(10, result.MinReturnedId);
        Assert.Equal(19, result.MaxReturnedId);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(7_000_000_032, result.RequestBodyBytes);
        Assert.Equal(58, result.ResponseBodyBytes);
        Assert.Equal(new[] { "", "a.example.test", "B.example.test" }, result.ByHost.Select(group => group.Host));
        Assert.Equal(new[] { 1, 1, 2 }, result.ByHost.Select(group => group.Count));
        Assert.Equal(new int?[] { null, 200, 404 }, result.ByStatus.Select(group => group.StatusCode));
        Assert.Equal(new[] { 1, 2, 1 }, result.ByStatus.Select(group => group.Count));
        Assert.Equal(result.Returned, result.ByHost.Sum(group => group.Count));
        Assert.Equal(result.Returned, result.ByStatus.Sum(group => group.Count));
        Assert.Equal(2, result.DurationMilliseconds.Count);
        Assert.Equal(10, result.DurationMilliseconds.Min);
        Assert.Equal(20, result.DurationMilliseconds.Max);
        Assert.Equal(15, result.DurationMilliseconds.Median);
        Assert.Equal(20, result.DurationMilliseconds.P95);
        Assert.Equal(new[] { 10, 12, 15, 19 }, response.Sessions.Select(session => session.Id));
        Assert.Equal(4, filters.Limit);
    }

    [Fact]
    public async Task EmptyPageHasZeroCountsAndNullTimingsAndIdBounds()
    {
        var result = await Summarize([]);

        Assert.Equal(0, result.TotalMatched);
        Assert.Equal(0, result.Returned);
        Assert.Equal(ProtocolConstants.DefaultSessionLimit, result.Limit);
        Assert.True(result.NewestFirst);
        Assert.False(result.Truncated);
        Assert.Null(result.MinReturnedId);
        Assert.Null(result.MaxReturnedId);
        Assert.Equal(0, result.CompletedCount);
        Assert.Equal(0, result.RequestBodyBytes);
        Assert.Equal(0, result.ResponseBodyBytes);
        Assert.Empty(result.ByHost);
        Assert.Empty(result.ByStatus);
        Assert.Equal(0, result.DurationMilliseconds.Count);
        Assert.Null(result.DurationMilliseconds.Min);
        Assert.Null(result.DurationMilliseconds.Max);
        Assert.Null(result.DurationMilliseconds.Median);
        Assert.Null(result.DurationMilliseconds.P95);
    }

    /// <summary>
    /// Verifies nearest-rank p95 and ordinary median for singleton, odd, even, and maximum-size pages.
    /// </summary>
    /// <param name="count">The number of completed timings, consisting of integers 1 through count.</param>
    /// <param name="median">The expected median.</param>
    /// <param name="p95">The expected nearest-rank p95.</param>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 2, 3)]
    [InlineData(4, 2.5, 4)]
    [InlineData(20, 10.5, 19)]
    [InlineData(21, 11, 20)]
    [InlineData(1000, 500.5, 950)]
    public async Task ComputesMedianAndNearestRankP95(int count, double median, double p95)
    {
        var result = await Summarize(Enumerable.Range(1, count).Reverse()
            .Select(value => new SessionSummary { Id = value, IsComplete = true, DurationMilliseconds = value })
            .ToList(), ProtocolConstants.MaxSessionLimit);

        Assert.False(result.Truncated);
        Assert.Equal(count, result.DurationMilliseconds.Count);
        Assert.Equal(1, result.DurationMilliseconds.Min);
        Assert.Equal(count, result.DurationMilliseconds.Max);
        Assert.Equal(median, result.DurationMilliseconds.Median);
        Assert.Equal(p95, result.DurationMilliseconds.P95);
    }

    [Fact]
    public async Task ExcludesUnavailableInvalidAndIncompleteTimingsButKeepsZero()
    {
        var result = await Summarize(
        [
            new() { IsComplete = true, DurationMilliseconds = 0 },
            new() { IsComplete = true },
            new() { IsComplete = true, DurationMilliseconds = -1 },
            new() { IsComplete = true, DurationMilliseconds = double.NaN },
            new() { IsComplete = true, DurationMilliseconds = double.PositiveInfinity },
            new() { IsComplete = true, DurationMilliseconds = double.NegativeInfinity },
            new() { IsComplete = false, DurationMilliseconds = 100 }
        ]);

        Assert.Equal(6, result.CompletedCount);
        Assert.Equal(1, result.DurationMilliseconds.Count);
        Assert.Equal(0, result.DurationMilliseconds.Min);
        Assert.Equal(0, result.DurationMilliseconds.Max);
        Assert.Equal(0, result.DurationMilliseconds.Median);
        Assert.Equal(0, result.DurationMilliseconds.P95);
        // Output remains valid JSON even if a peer supplies invalid non-finite timing metadata.
        Assert.Contains("\"count\":1", JsonSerializer.Serialize(result.DurationMilliseconds, JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task NonemptyPageWithoutCompletedTimingsDoesNotInventZeroMeasurements()
    {
        var result = await Summarize([new() { IsComplete = true }, new() { DurationMilliseconds = 10 }]);

        Assert.Equal(2, result.Returned);
        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(0, result.DurationMilliseconds.Count);
        Assert.Null(result.DurationMilliseconds.Min);
        Assert.Null(result.DurationMilliseconds.Max);
        Assert.Null(result.DurationMilliseconds.Median);
        Assert.Null(result.DurationMilliseconds.P95);
    }

    [Fact]
    public async Task EvenMedianAvoidsOverflowForFiniteLargeTimings()
    {
        var result = await Summarize(
        [
            new() { IsComplete = true, DurationMilliseconds = double.MaxValue },
            new() { IsComplete = true, DurationMilliseconds = double.MaxValue }
        ]);

        Assert.Equal(double.MaxValue, result.DurationMilliseconds.Median);
    }

    /// <summary>
    /// Rejects invalid metadata bounds and search filters before any bridge operation.
    /// </summary>
    /// <param name="field">The list contract field to replace.</param>
    /// <param name="value">The invalid field value.</param>
    [Theory]
    [InlineData(nameof(ListSessionsRequest.Limit), 0)]
    [InlineData(nameof(ListSessionsRequest.Limit), 1001)]
    [InlineData(nameof(ListSessionsRequest.MinId), -1)]
    [InlineData(nameof(ListSessionsRequest.MaxId), -1)]
    [InlineData(nameof(ListSessionsRequest.MinDurationMilliseconds), -1.0)]
    [InlineData(nameof(ListSessionsRequest.MaxDurationMilliseconds), -1.0)]
    [InlineData(nameof(ListSessionsRequest.MinDurationMilliseconds), double.NaN)]
    [InlineData(nameof(ListSessionsRequest.MaxDurationMilliseconds), double.NaN)]
    [InlineData(nameof(ListSessionsRequest.MinDurationMilliseconds), double.PositiveInfinity)]
    [InlineData(nameof(ListSessionsRequest.MaxDurationMilliseconds), double.NegativeInfinity)]
    [InlineData(nameof(ListSessionsRequest.MinBodyBytes), -1L)]
    [InlineData(nameof(ListSessionsRequest.MaxBodyBytes), -1L)]
    [InlineData(nameof(ListSessionsRequest.HeaderName), "X-Test")]
    [InlineData(nameof(ListSessionsRequest.HeaderValue), "test-value")]
    [InlineData(nameof(ListSessionsRequest.BodyContains), "needle")]
    [InlineData(nameof(ListSessionsRequest.BodyContains), " ")]
    public async Task RejectsInvalidOrNonMetadataFiltersBeforeBridgeCall(string field, object value)
    {
        var filters = new ListSessionsRequest();
        typeof(ListSessionsRequest).GetProperty(field)!.SetValue(filters, value);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => new SessionSummaryService(new TestBridgeClient())
            .SummarizeAsync(filters, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsInvertedBoundsAndNullFiltersBeforeBridgeCall()
    {
        var service = new SessionSummaryService(new TestBridgeClient());
        foreach (var filters in new ListSessionsRequest[]
        {
            new() { MinId = 2, MaxId = 1 },
            new() { MinDurationMilliseconds = 2, MaxDurationMilliseconds = 1 },
            new() { MinBodyBytes = 2, MaxBodyBytes = 1 }
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.SummarizeAsync(filters, TestContext.Current.CancellationToken));
        }

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SummarizeAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsOversizedOrInconsistentBridgeMetadata()
    {
        foreach (var response in new ListSessionsResponse[]
        {
            new() { TotalMatched = 2, Sessions = [new(), new()] },
            new() { TotalMatched = 0, Sessions = [new()] },
            new() { TotalMatched = -1 },
            new() { Sessions = null! },
            new() { TotalMatched = 1, Sessions = [null!] },
            new() { TotalMatched = 1, Sessions = [new() { RequestBodyBytes = -1 }] },
            new() { TotalMatched = 1, Sessions = [new() { ResponseBodyBytes = -1 }] }
        })
        {
            var bridge = new TestBridgeClient { Handler = (_, _) => response };
            var error = await Assert.ThrowsAsync<BridgeClientException>(() => new SessionSummaryService(bridge)
                .SummarizeAsync(new ListSessionsRequest { Limit = 1 }, TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
        }
    }

    /// <summary>
    /// Preserves stable bridge failures and rejects empty or partial aggregates on failure.
    /// </summary>
    /// <param name="code">The bridge's stable failure code.</param>
    [Theory]
    [InlineData(ErrorCodes.Unavailable)]
    [InlineData(ErrorCodes.Timeout)]
    [InlineData(ErrorCodes.ProtocolMismatch)]
    public async Task PreservesBridgeErrors(string code)
    {
        var expected = new BridgeClientException(code, "Synthetic failure.");
        var bridge = new TestBridgeClient { Handler = (_, _) => throw expected };

        var actual = await Assert.ThrowsAsync<BridgeClientException>(() => new SessionSummaryService(bridge)
            .SummarizeAsync(new ListSessionsRequest(), TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CancellationReachesTheSinglePendingBridgeCall()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var bridge = new PendingBridgeClient();
        var pending = new SessionSummaryService(bridge).SummarizeAsync(new ListSessionsRequest(), cancellation.Token);

        Assert.Equal(cancellation.Token, bridge.ObservedToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, bridge.Calls);
    }

    [Fact]
    public async Task PreCanceledRequestNeverCallsBridge()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SessionSummaryService(new TestBridgeClient())
            .SummarizeAsync(new ListSessionsRequest(), cancellation.Token));
    }

    private static Task<SessionSummaryResult> Summarize(List<SessionSummary> sessions, int limit = ProtocolConstants.DefaultSessionLimit)
    {
        var bridge = new TestBridgeClient
        {
            Handler = (operation, _) =>
            {
                Assert.Equal(Operations.ListSessions, operation);
                return new ListSessionsResponse { TotalMatched = sessions.Count, Sessions = sessions };
            }
        };
        return new SessionSummaryService(bridge).SummarizeAsync(new ListSessionsRequest { Limit = limit }, TestContext.Current.CancellationToken);
    }

    private sealed class PendingBridgeClient : IBridgeClient
    {
        public int Calls { get; private set; }
        public CancellationToken ObservedToken { get; private set; }

        public async Task<TResponse> SendAsync<TRequest, TResponse>(
            string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Operations.ListSessions, operation);
            Calls++;
            ObservedToken = cancellationToken;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The pending test bridge call must be canceled.");
        }
    }
}
