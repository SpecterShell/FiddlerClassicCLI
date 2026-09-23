// Projects and compares session metadata, ordered headers, and body hashes within UI-thread operations.
using System.Globalization;
using System.Security.Cryptography;
using Fiddler;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeDispatcher
{
    /// <summary>
    /// Compares stable metadata, exact ordered headers, and request and response body hashes.
    /// </summary>
    /// <param name="request">The two captured session IDs to compare.</param>
    private static SessionDiffResponse DiffSessions(DiffSessionsRequest request)
    {
        return FiddlerThread.Invoke(() =>
        {
            var left = FindSession(request.LeftSessionId);
            var right = FindSession(request.RightSessionId);
            var result = new SessionDiffResponse
            {
                LeftSessionId = left.id,
                RightSessionId = right.id
            };

            AddDifference(result, "metadata", "method", left.RequestMethod, right.RequestMethod);
            AddDifference(result, "metadata", "url", left.fullUrl, right.fullUrl);
            AddDifference(result, "metadata", "status", left.responseCode.ToString(CultureInfo.InvariantCulture), right.responseCode.ToString(CultureInfo.InvariantCulture));
            AddDifference(result, "metadata", "content-type", GetHeader(left.ResponseHeaders, "Content-Type"), GetHeader(right.ResponseHeaders, "Content-Type"));
            AddDifference(result, "metadata", "duration-ms", FormatDuration(left), FormatDuration(right));
            AddHeaderDifferences(result, "request-header", ToHeaders(left.RequestHeaders), ToHeaders(right.RequestHeaders));
            AddHeaderDifferences(result, "response-header", ToHeaders(left.ResponseHeaders), ToHeaders(right.ResponseHeaders));
            AddDifference(result, "body", "request-sha256", ComputeSha256(left.requestBodyBytes), ComputeSha256(right.requestBodyBytes));
            AddDifference(result, "body", "response-sha256", ComputeSha256(left.responseBodyBytes), ComputeSha256(right.responseBodyBytes));
            return result;
        });
    }

    private static double GetDuration(Session session)
    {
        var started = session.Timers.ClientBeginRequest;
        var ended = session.Timers.ClientDoneResponse;
        return started.Year > 1900 && ended >= started ? (ended - started).TotalMilliseconds : 0;
    }

    private static string FormatDuration(Session session)
    {
        return GetDuration(session).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool IsError(Session session)
    {
        return session.state == SessionStates.Aborted || session.responseCode >= 400;
    }

    private static string ComputeSha256(byte[]? body)
    {
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(body ?? Array.Empty<byte>())).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void AddDifference(SessionDiffResponse result, string area, string name, string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return;
        }

        result.Differences.Add(new SessionDiffEntry
        {
            Area = area,
            Name = name,
            LeftValues = left == null ? Array.Empty<string>() : new[] { left },
            RightValues = right == null ? Array.Empty<string>() : new[] { right }
        });
    }

    private static void AddHeaderDifferences(
        SessionDiffResponse result,
        string area,
        IEnumerable<HeaderDto> left,
        IEnumerable<HeaderDto> right)
    {
        var leftHeaders = left.ToArray();
        var rightHeaders = right.ToArray();
        var initialDifferenceCount = result.Differences.Count;
        var leftGroups = leftHeaders.GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(header => header.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
        var rightGroups = rightHeaders.GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(header => header.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in leftGroups.Keys.Union(rightGroups.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var leftValues = leftGroups.TryGetValue(name, out var foundLeft) ? foundLeft : Array.Empty<string>();
            var rightValues = rightGroups.TryGetValue(name, out var foundRight) ? foundRight : Array.Empty<string>();
            if (!leftValues.SequenceEqual(rightValues, StringComparer.Ordinal))
            {
                result.Differences.Add(new SessionDiffEntry
                {
                    Area = area,
                    Name = name,
                    LeftValues = leftValues,
                    RightValues = rightValues
                });
            }
        }

        var leftOrdered = leftHeaders.Select(FormatHeader).ToArray();
        var rightOrdered = rightHeaders.Select(FormatHeader).ToArray();
        if (result.Differences.Count == initialDifferenceCount
            && !leftOrdered.SequenceEqual(rightOrdered, StringComparer.Ordinal))
        {
            result.Differences.Add(new SessionDiffEntry
            {
                Area = area,
                Name = "ordered-sequence",
                LeftValues = leftOrdered,
                RightValues = rightOrdered
            });
        }
    }

    private static string FormatHeader(HeaderDto header)
    {
        return $"{header.Name}: {header.Value}";
    }

    /// <summary>
    /// Projects a Fiddler session into transport-safe metadata without including headers or bodies.
    /// </summary>
    /// <param name="session">The Fiddler session to project.</param>
    private static SessionSummary ToSummary(Session session)
    {
        var started = session.Timers.ClientBeginRequest;
        var ended = session.Timers.ClientDoneResponse;
        double? duration = null;
        if (started.Year > 1900 && ended >= started)
        {
            duration = (ended - started).TotalMilliseconds;
        }

        return new SessionSummary
        {
            Id = session.id,
            State = session.state.ToString(),
            IsComplete = session.state == SessionStates.Done,
            Method = session.RequestMethod ?? string.Empty,
            Url = session.fullUrl ?? string.Empty,
            Host = session.hostname ?? string.Empty,
            PathAndQuery = session.PathAndQuery ?? string.Empty,
            Scheme = session.RequestHeaders?.UriScheme ?? (session.isHTTPS ? Uri.UriSchemeHttps : Uri.UriSchemeHttp),
            Protocol = session.RequestHeaders?.HTTPVersion,
            StatusCode = session.responseCode > 0 ? session.responseCode : (int?)null,
            StatusDescription = session.ResponseHeaders?.StatusDescription,
            ContentType = GetHeader(session.ResponseHeaders, "Content-Type"),
            Process = session.LocalProcess,
            ProcessId = session.LocalProcessID,
            ClientIp = session.clientIP,
            ServerIp = session.m_hostIP,
            RequestBodyBytes = session.requestBodyBytes?.LongLength ?? 0,
            ResponseBodyBytes = session.responseBodyBytes?.LongLength ?? 0,
            StartedAtUtc = started.Year > 1900
                ? started.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : null,
            DurationMilliseconds = duration,
            IsError = IsError(session),
            HasWebSocketMessages = session.bHasWebSocketMessages
        };
    }

    /// <summary>
    /// Copies Fiddler headers in their original order and without normalization.
    /// </summary>
    /// <param name="headers">The request or response headers to copy.</param>
    private static List<HeaderDto> ToHeaders(HTTPHeaders? headers)
    {
        var result = new List<HeaderDto>();
        if (headers == null)
        {
            return result;
        }

        for (var index = 0; index < headers.Count(); index++)
        {
            var header = headers[index];
            result.Add(new HeaderDto { Name = header.Name, Value = header.Value });
        }

        return result;
    }

    private static string? GetHeader(HTTPHeaders? headers, string name)
    {
        return headers == null ? null : headers[name];
    }
}
