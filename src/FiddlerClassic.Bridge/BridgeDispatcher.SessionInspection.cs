// Inspects and filters UI-owned sessions while copying only the requested body byte ranges.
using System.Text;
using Fiddler;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge;

internal sealed partial class BridgeDispatcher
{
    /// <summary>
    /// Filters the current session snapshot and returns a bounded ordered summary list.
    /// </summary>
    /// <param name="request">The filter, ordering, ID bounds, and result limit.</param>
    private static ListSessionsResponse ListSessions(ListSessionsRequest request)
    {
        ValidateFilters(request);

        return FiddlerThread.Invoke(() =>
        {
            var matched = ApplyFilters(FiddlerApplication.UI.GetAllSessions(), request).ToArray();
            var ordered = request.NewestFirst
                ? matched.OrderByDescending(session => session.id)
                : matched.OrderBy(session => session.id);

            return new ListSessionsResponse
            {
                TotalMatched = matched.Length,
                Sessions = ordered.Take(request.Limit).Select(ToSummary).ToList()
            };
        });
    }

    /// <summary>
    /// Returns metadata and optional exact headers for one captured session.
    /// </summary>
    /// <param name="request">The session ID and header inclusion choice.</param>
    private static SessionDetails GetSessionDetails(GetSessionDetailsRequest request)
    {
        return FiddlerThread.Invoke(() =>
        {
            var session = FindSession(request.SessionId);
            return new SessionDetails
            {
                Summary = ToSummary(session),
                RequestHeaders = request.IncludeHeaders ? ToHeaders(session.RequestHeaders) : null,
                ResponseHeaders = request.IncludeHeaders ? ToHeaders(session.ResponseHeaders) : null
            };
        });
    }

    /// <summary>
    /// Copies a validated range from one request or response body without retaining additional payload data.
    /// </summary>
    /// <param name="request">The session, direction, byte offset, and bounded length to read.</param>
    private static SessionBodyChunk GetSessionBody(GetSessionBodyRequest request)
    {
        if (request.Offset < 0)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Body offset cannot be negative.");
        }

        if (request.Length < 1 || request.Length > ProtocolConstants.CliBodyChunkBytes)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"Body length must be between 1 and {ProtocolConstants.CliBodyChunkBytes} bytes.");
        }

        if (!string.Equals(request.Direction, BodyDirections.Request, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Direction, BodyDirections.Response, StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Body direction must be 'request' or 'response'.");
        }

        return FiddlerThread.Invoke(() =>
        {
            var session = FindSession(request.SessionId);
            var isRequest = string.Equals(request.Direction, BodyDirections.Request, StringComparison.OrdinalIgnoreCase);
            var body = isRequest ? session.requestBodyBytes : session.responseBodyBytes;
            body = body ?? Array.Empty<byte>();

            if (request.Offset > body.LongLength)
            {
                throw new BridgeOperationException(
                    ErrorCodes.InvalidRequest,
                    $"Body offset {request.Offset} exceeds body length {body.LongLength}.");
            }

            var count = (int)Math.Min(request.Length, body.LongLength - request.Offset);
            var chunk = new byte[count];
            if (count > 0)
            {
                Buffer.BlockCopy(body, checked((int)request.Offset), chunk, 0, count);
            }

            return new SessionBodyChunk
            {
                SessionId = session.id,
                Direction = isRequest ? BodyDirections.Request : BodyDirections.Response,
                Offset = request.Offset,
                BytesReturned = count,
                TotalBytes = body.LongLength,
                EndOfBody = request.Offset + count >= body.LongLength,
                Base64Data = Convert.ToBase64String(chunk),
                ContentType = GetHeader(
                    isRequest ? (HTTPHeaders)session.RequestHeaders : session.ResponseHeaders,
                    "Content-Type")
            };
        });
    }

    /// <summary>
    /// Applies all metadata, header, timing, size, error, and bounded body filters to a session sequence.
    /// </summary>
    /// <param name="sessions">The snapshot to filter.</param>
    /// <param name="request">The reusable filter contract.</param>
    private static IEnumerable<Session> ApplyFilters(IEnumerable<Session> sessions, ListSessionsRequest request)
    {
        var query = sessions;
        if (request.MinId.HasValue)
        {
            query = query.Where(session => session.id >= request.MinId.Value);
        }

        if (request.MaxId.HasValue)
        {
            query = query.Where(session => session.id <= request.MaxId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Method))
        {
            query = query.Where(session => string.Equals(session.RequestMethod, request.Method, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.Host))
        {
            query = query.Where(session => Contains(session.hostname, request.Host!));
        }

        if (!string.IsNullOrWhiteSpace(request.UrlContains))
        {
            query = query.Where(session => Contains(session.fullUrl, request.UrlContains!));
        }

        if (request.StatusCode.HasValue)
        {
            query = query.Where(session => session.responseCode == request.StatusCode.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.ContentType))
        {
            query = query.Where(session => Contains(GetHeader(session.ResponseHeaders, "Content-Type"), request.ContentType!));
        }

        if (!string.IsNullOrWhiteSpace(request.Process))
        {
            query = query.Where(session => Contains(session.LocalProcess, request.Process!));
        }

        if (!string.IsNullOrWhiteSpace(request.Protocol))
        {
            query = query.Where(session => string.Equals(
                session.RequestHeaders?.HTTPVersion,
                request.Protocol,
                StringComparison.OrdinalIgnoreCase));
        }

        if (request.MinDurationMilliseconds.HasValue)
        {
            query = query.Where(session => GetDuration(session) >= request.MinDurationMilliseconds.Value);
        }

        if (request.MaxDurationMilliseconds.HasValue)
        {
            query = query.Where(session => GetDuration(session) <= request.MaxDurationMilliseconds.Value);
        }

        if (request.MinBodyBytes.HasValue)
        {
            query = query.Where(session => GetCombinedBodyLength(session) >= request.MinBodyBytes.Value);
        }

        if (request.MaxBodyBytes.HasValue)
        {
            query = query.Where(session => GetCombinedBodyLength(session) <= request.MaxBodyBytes.Value);
        }

        if (request.IsError.HasValue)
        {
            query = query.Where(session => IsError(session) == request.IsError.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.HeaderName) || !string.IsNullOrWhiteSpace(request.HeaderValue))
        {
            query = query.Where(session => HeadersMatch(session, request.HeaderName, request.HeaderValue));
        }

        if (!string.IsNullOrEmpty(request.BodyContains))
        {
            var needle = Encoding.UTF8.GetBytes(request.BodyContains);
            query = query.Where(session => BodyContains(session, request.BodyDirection, needle, request.BodySearchBytes));
        }

        return query;
    }

    /// <summary>
    /// Rejects invalid combinations and bounds before a filter touches Fiddler state.
    /// </summary>
    /// <param name="request">The filter contract to validate.</param>
    private static void ValidateFilters(ListSessionsRequest request)
    {
        if (request.Limit < 1 || request.Limit > ProtocolConstants.MaxSessionLimit)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"Limit must be between 1 and {ProtocolConstants.MaxSessionLimit}.");
        }

        if (request.MinId < 0 || request.MaxId < 0 || (request.MinId.HasValue && request.MaxId.HasValue && request.MinId > request.MaxId))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Session ID bounds are invalid.");
        }

        if (request.MinDurationMilliseconds < 0 || request.MaxDurationMilliseconds < 0
            || (request.MinDurationMilliseconds.HasValue && request.MaxDurationMilliseconds.HasValue
                && request.MinDurationMilliseconds > request.MaxDurationMilliseconds))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Duration bounds are invalid.");
        }

        if (request.MinBodyBytes < 0 || request.MaxBodyBytes < 0
            || (request.MinBodyBytes.HasValue && request.MaxBodyBytes.HasValue && request.MinBodyBytes > request.MaxBodyBytes))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Body-size bounds are invalid.");
        }

        if (!string.IsNullOrEmpty(request.BodyContains))
        {
            if (request.BodySearchBytes < 1 || request.BodySearchBytes > ProtocolConstants.MaxBodySearchBytes)
            {
                throw new BridgeOperationException(
                    ErrorCodes.InvalidRequest,
                    $"Body search bytes must be between 1 and {ProtocolConstants.MaxBodySearchBytes}.");
            }

            if (!string.Equals(request.BodyDirection, BodyDirections.Request, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(request.BodyDirection, BodyDirections.Response, StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Body search direction must be 'request' or 'response'.");
            }
        }
    }

    private static bool HeadersMatch(Session session, string? name, string? value)
    {
        return ToHeaders(session.RequestHeaders)
            .Concat(ToHeaders(session.ResponseHeaders))
            .Any(header => (string.IsNullOrWhiteSpace(name) || string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(value) || Contains(header.Value, value!)));
    }

    private static bool BodyContains(Session session, string direction, byte[] needle, int maxBytes)
    {
        var source = string.Equals(direction, BodyDirections.Request, StringComparison.OrdinalIgnoreCase)
            ? session.requestBodyBytes
            : session.responseBodyBytes;
        source = source ?? Array.Empty<byte>();
        var length = Math.Min(source.Length, maxBytes);
        if (needle.Length == 0 || needle.Length > length)
        {
            return needle.Length == 0;
        }

        for (var offset = 0; offset <= length - needle.Length; offset++)
        {
            var matched = true;
            for (var index = 0; index < needle.Length; index++)
            {
                if (source[offset + index] != needle[index])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static long GetCombinedBodyLength(Session session)
    {
        return (session.requestBodyBytes?.LongLength ?? 0) + (session.responseBodyBytes?.LongLength ?? 0);
    }

    /// <summary>
    /// Resolves a positive session ID from Fiddler's current UI session list.
    /// </summary>
    /// <param name="sessionId">The Fiddler session ID.</param>
    private static Session FindSession(int sessionId)
    {
        if (sessionId <= 0)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Session ID must be positive.");
        }

        return FiddlerApplication.UI.GetAllSessions().FirstOrDefault(session => session.id == sessionId)
            ?? throw new BridgeOperationException(ErrorCodes.NotFound, $"Session {sessionId} was not found.");
    }

    private static bool Contains(string? value, string expected)
    {
        return value != null && value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
