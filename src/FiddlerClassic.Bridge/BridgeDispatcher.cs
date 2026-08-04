// Dispatches versioned bridge operations onto Fiddler Classic's UI-owned session state.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Fiddler;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge;

internal sealed class BridgeDispatcher : IDisposable
{
    private const int CompletedSessionBufferCapacity = 2048;
    private readonly object _completionLock = new object();
    private readonly Queue<int> _completedSessionIds = new Queue<int>();
    private readonly AutoResponderController _autoResponder = new AutoResponderController();
    private readonly BreakpointController _breakpoints = new BreakpointController();
    private long _completionSequence;
    private bool _disposed;

    /// <summary>
    /// Subscribes to completed-session notifications used by bounded wait operations.
    /// </summary>
    public BridgeDispatcher()
    {
        FiddlerApplication.AfterSessionComplete += OnAfterSessionComplete;
    }

    /// <summary>
    /// Validates an envelope, dispatches its operation, and converts all failures into stable bridge responses.
    /// </summary>
    /// <param name="request">The versioned request envelope received from the host.</param>
    public BridgeResponse Dispatch(BridgeRequest request)
    {
        if (request.ProtocolVersion != ProtocolConstants.Version)
        {
            return Failure(
                request.RequestId,
                ErrorCodes.ProtocolMismatch,
                $"Protocol version {request.ProtocolVersion} is unsupported; expected {ProtocolConstants.Version}.");
        }

        try
        {
            object result;
            switch (request.Operation)
            {
                case Operations.GetStatus:
                    result = GetStatus();
                    break;
                case Operations.SetCapture:
                    result = SetCapture(ReadPayload<SetCaptureRequest>(request));
                    break;
                case Operations.ListSessions:
                    result = ListSessions(ReadPayload<ListSessionsRequest>(request));
                    break;
                case Operations.WaitForSession:
                    result = WaitForSession(ReadPayload<WaitForSessionRequest>(request));
                    break;
                case Operations.GetSessionDetails:
                    result = GetSessionDetails(ReadPayload<GetSessionDetailsRequest>(request));
                    break;
                case Operations.GetSessionBody:
                    result = GetSessionBody(ReadPayload<GetSessionBodyRequest>(request));
                    break;
                case Operations.ClearSessions:
                    result = ClearSessions(ReadPayload<ClearSessionsRequest>(request));
                    break;
                case Operations.RemoveSessions:
                    result = RemoveSessions(ReadPayload<RemoveSessionsRequest>(request));
                    break;
                case Operations.SaveSessions:
                    result = SaveSessions(ReadPayload<SaveSessionsRequest>(request));
                    break;
                case Operations.LoadSessions:
                    result = LoadSessions(ReadPayload<LoadSessionsRequest>(request));
                    break;
                case Operations.ReplaySession:
                    result = ReplaySession(ReadPayload<ReplaySessionRequest>(request));
                    break;
                case Operations.SendRequest:
                    result = SendRequest(ReadPayload<SendRequestRequest>(request));
                    break;
                case Operations.DiffSessions:
                    result = DiffSessions(ReadPayload<DiffSessionsRequest>(request));
                    break;
                case Operations.ListWebSocketMessages:
                    result = ListWebSocketMessages(ReadPayload<ListWebSocketMessagesRequest>(request));
                    break;
                case Operations.GetWebSocketMessage:
                    result = GetWebSocketMessage(ReadPayload<GetWebSocketMessageRequest>(request));
                    break;
                case Operations.GetAutoResponder:
                    result = _autoResponder.GetStatus();
                    break;
                case Operations.ConfigureAutoResponder:
                    result = _autoResponder.Configure(ReadPayload<ConfigureAutoResponderRequest>(request));
                    break;
                case Operations.ListAutoResponderRules:
                    result = _autoResponder.ListRules();
                    break;
                case Operations.AddAutoResponderRule:
                    result = _autoResponder.AddRule(ReadPayload<AddAutoResponderRuleRequest>(request));
                    break;
                case Operations.UpdateAutoResponderRule:
                    result = _autoResponder.UpdateRule(ReadPayload<UpdateAutoResponderRuleRequest>(request));
                    break;
                case Operations.MoveAutoResponderRule:
                    result = _autoResponder.MoveRule(ReadPayload<MoveAutoResponderRuleRequest>(request));
                    break;
                case Operations.RemoveAutoResponderRule:
                    result = _autoResponder.RemoveRule(ReadPayload<RemoveAutoResponderRuleRequest>(request));
                    break;
                case Operations.ClearAutoResponderRules:
                    result = _autoResponder.ClearRules(ReadPayload<ClearAutoResponderRulesRequest>(request));
                    break;
                case Operations.SaveAutoResponderRules:
                    result = _autoResponder.SaveRules(ReadPayload<SaveAutoResponderRulesRequest>(request));
                    break;
                case Operations.LoadAutoResponderRules:
                    result = _autoResponder.LoadRules(ReadPayload<LoadAutoResponderRulesRequest>(request));
                    break;
                case Operations.ListBreakpointArms:
                    result = _breakpoints.ListArms();
                    break;
                case Operations.ArmBreakpoint:
                    result = _breakpoints.Arm(ReadPayload<ArmBreakpointRequest>(request));
                    break;
                case Operations.DisarmBreakpoint:
                    result = _breakpoints.Disarm(ReadPayload<DisarmBreakpointRequest>(request));
                    break;
                case Operations.ListPendingBreakpoints:
                    result = _breakpoints.ListPending(ReadPayload<ListPendingBreakpointsRequest>(request));
                    break;
                case Operations.WaitForBreakpoint:
                    result = _breakpoints.Wait(ReadPayload<WaitForBreakpointRequest>(request));
                    break;
                case Operations.GetBreakpoint:
                    result = _breakpoints.Get(ReadPayload<GetBreakpointRequest>(request));
                    break;
                case Operations.UpdateBreakpoint:
                    result = _breakpoints.Update(ReadPayload<UpdateBreakpointRequest>(request));
                    break;
                case Operations.ResumeBreakpoint:
                    result = _breakpoints.Resume(ReadPayload<BreakpointActionRequest>(request));
                    break;
                case Operations.AbortBreakpoint:
                    result = _breakpoints.Abort(ReadPayload<BreakpointActionRequest>(request));
                    break;
                default:
                    throw new BridgeOperationException(
                        ErrorCodes.InvalidRequest,
                        $"Unknown bridge operation '{request.Operation}'.");
            }

            return Success(request.RequestId, result);
        }
        catch (BridgeOperationException exception)
        {
            return Failure(request.RequestId, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            FiddlerApplication.Log.LogString($"[FiddlerClassicCLI] Operation '{request.Operation}' failed: {exception}");
            return Failure(request.RequestId, ErrorCodes.Internal, "The Fiddler bridge operation failed.");
        }
    }

    /// <summary>
    /// Unsubscribes completion tracking and wakes pending waits during extension shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FiddlerApplication.AfterSessionComplete -= OnAfterSessionComplete;
        _breakpoints.Dispose();
        lock (_completionLock)
        {
            Monitor.PulseAll(_completionLock);
        }
    }

    /// <summary>
    /// Captures Fiddler, proxy, decryption, and session state from the UI thread.
    /// </summary>
    private static StatusResponse GetStatus()
    {
        return OnUi(() =>
        {
            var sessions = FiddlerApplication.UI.GetAllSessions();
            var proxy = FiddlerApplication.oProxy;
            return new StatusResponse
            {
                FiddlerInstalled = true,
                FiddlerRunning = true,
                FiddlerProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                FiddlerVersion = FiddlerApplication.GetVersionString(),
                BridgeInstalled = true,
                BridgeConnected = true,
                BridgeVersion = typeof(BridgeExtension).Assembly.GetName().Version?.ToString(),
                IsProxyAttached = proxy != null && IsProxyAttached(proxy),
                IsListening = proxy != null && proxy.IsListening,
                ListenPort = proxy?.ListenPort,
                IsHttpsDecryptionEnabled = CONFIG.DecryptHTTPS,
                SessionCount = sessions.Length,
                CompletedSessionCount = sessions.Count(session => session.state == SessionStates.Done)
            };
        });
    }

    /// <summary>
    /// Attaches or detaches Fiddler as the system proxy on the UI thread.
    /// </summary>
    /// <param name="request">The desired proxy attachment state.</param>
    private static CaptureResponse SetCapture(SetCaptureRequest request)
    {
        return OnUi(() =>
        {
            var proxy = FiddlerApplication.oProxy
                ?? throw new BridgeOperationException(ErrorCodes.Unavailable, "Fiddler's proxy is not available.");

            if (request.Enabled && !IsProxyAttached(proxy))
            {
                FiddlerApplication.UI.actAttachProxy();
            }
            else if (!request.Enabled && IsProxyAttached(proxy))
            {
                FiddlerApplication.UI.actDetachProxy();
            }

            return new CaptureResponse { IsProxyAttached = IsProxyAttached(proxy) };
        });
    }

    /// <summary>
    /// Filters the current session snapshot and returns a bounded ordered summary list.
    /// </summary>
    /// <param name="request">The filter, ordering, ID bounds, and result limit.</param>
    private static ListSessionsResponse ListSessions(ListSessionsRequest request)
    {
        ValidateFilters(request);

        return OnUi(() =>
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
    /// Waits for the first completed session after a cursor that satisfies the reusable list filters.
    /// </summary>
    /// <param name="request">The exclusive ID cursor, filters, and bounded timeout.</param>
    private WaitForSessionResponse WaitForSession(WaitForSessionRequest request)
    {
        if (request.AfterId < 0)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The after-session ID cannot be negative.");
        }

        if (request.TimeoutMilliseconds < 1 || request.TimeoutMilliseconds > ProtocolConstants.MaxWaitMilliseconds)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"Wait timeout must be between 1 and {ProtocolConstants.MaxWaitMilliseconds} milliseconds.");
        }

        request.Filters = request.Filters ?? new ListSessionsRequest();
        request.Filters.MinId = Math.Max(request.Filters.MinId ?? 0, request.AfterId + 1);
        request.Filters.NewestFirst = false;
        request.Filters.Limit = 1;
        ValidateFilters(request.Filters);

        var deadline = DateTime.UtcNow.AddMilliseconds(request.TimeoutMilliseconds);
        while (!_disposed)
        {
            long observedSequence;
            lock (_completionLock)
            {
                observedSequence = _completionSequence;
            }

            var inspection = OnUi(() =>
            {
                var snapshot = FiddlerApplication.UI.GetAllSessions();
                var latestId = snapshot.Length == 0 ? request.AfterId : snapshot.Max(session => session.id);
                var match = ApplyFilters(snapshot, request.Filters)
                    .Where(session => session.state == SessionStates.Done)
                    .OrderBy(session => session.id)
                    .FirstOrDefault();
                return new WaitForSessionResponse
                {
                    Matched = match != null,
                    Session = match == null ? null : ToSummary(match),
                    LatestSessionId = latestId
                };
            });
            if (inspection.Matched)
            {
                return inspection;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return inspection;
            }

            lock (_completionLock)
            {
                if (_completionSequence == observedSequence && !_disposed)
                {
                    var fallbackInterval = TimeSpan.FromMilliseconds(250);
                    Monitor.Wait(_completionLock, remaining < fallbackInterval ? remaining : fallbackInterval);
                }
            }
        }

        throw new BridgeOperationException(ErrorCodes.Unavailable, "The Fiddler bridge is shutting down.");
    }

    /// <summary>
    /// Returns metadata and optional exact headers for one captured session.
    /// </summary>
    /// <param name="request">The session ID and header inclusion choice.</param>
    private static SessionDetails GetSessionDetails(GetSessionDetailsRequest request)
    {
        return OnUi(() =>
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

        return OnUi(() =>
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
    /// Removes all sessions after verifying the caller supplied explicit confirmation.
    /// </summary>
    /// <param name="request">The destructive-operation confirmation.</param>
    private static ClearSessionsResponse ClearSessions(ClearSessionsRequest request)
    {
        if (!request.Confirm)
        {
            throw new BridgeOperationException(
                ErrorCodes.ConfirmationRequired,
                "Clearing sessions requires explicit confirmation.");
        }

        return OnUi(() =>
        {
            var before = new HashSet<int>(FiddlerApplication.UI.GetAllSessions().Select(session => session.id));
            FiddlerApplication.UI.actRemoveAllSessions();
            var remaining = new HashSet<int>(FiddlerApplication.UI.GetAllSessions().Select(session => session.id));
            before.ExceptWith(remaining);
            return new ClearSessionsResponse { RemovedCount = before.Count };
        });
    }

    /// <summary>
    /// Removes an exact, bounded set of sessions after explicit confirmation.
    /// </summary>
    /// <param name="request">The session IDs and destructive-operation confirmation.</param>
    private static RemoveSessionsResponse RemoveSessions(RemoveSessionsRequest request)
    {
        if (!request.Confirm)
        {
            throw new BridgeOperationException(
                ErrorCodes.ConfirmationRequired,
                "Removing sessions requires explicit confirmation.");
        }

        var ids = (request.SessionIds ?? Array.Empty<int>()).Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0 || ids.Length > ProtocolConstants.MaxSessionLimit || ids.Any(id => id <= 0))
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"Provide between 1 and {ProtocolConstants.MaxSessionLimit} positive session IDs.");
        }

        return OnUi(() =>
        {
            var requested = new HashSet<int>(ids);
            var selected = FiddlerApplication.UI.GetAllSessions()
                .Where(session => requested.Contains(session.id))
                .ToArray();
            var missing = requested.Except(selected.Select(session => session.id)).OrderBy(id => id).ToArray();
            if (missing.Length > 0)
            {
                throw new BridgeOperationException(ErrorCodes.NotFound, $"Sessions not found: {string.Join(", ", missing)}");
            }

            FiddlerApplication.UI.actRemoveRange(selected);
            return new RemoveSessionsResponse
            {
                RemovedCount = selected.Length,
                RemovedSessionIds = ids
            };
        });
    }

    /// <summary>
    /// Selects sessions, enforces overwrite confirmation, and exports them as a SAZ archive.
    /// </summary>
    /// <param name="request">The archive path, optional session IDs, and overwrite controls.</param>
    private static ArchiveResponse SaveSessions(SaveSessionsRequest request)
    {
        var path = ValidateArchivePath(request.Path, mustExist: false);
        if (File.Exists(path))
        {
            if (!request.Overwrite)
            {
                throw new BridgeOperationException(ErrorCodes.Conflict, $"Archive already exists: {path}");
            }

            if (!request.ConfirmOverwrite)
            {
                throw new BridgeOperationException(
                    ErrorCodes.ConfirmationRequired,
                    "Overwriting an existing archive requires explicit confirmation.");
            }
        }

        return OnUi(() =>
        {
            var allSessions = FiddlerApplication.UI.GetAllSessions();
            Session[] selected;

            if (request.SessionIds == null || request.SessionIds.Length == 0)
            {
                selected = allSessions;
            }
            else
            {
                var requestedIds = new HashSet<int>(request.SessionIds);
                selected = allSessions.Where(session => requestedIds.Contains(session.id)).ToArray();
                var missing = requestedIds.Except(selected.Select(session => session.id)).OrderBy(id => id).ToArray();
                if (missing.Length > 0)
                {
                    throw new BridgeOperationException(
                        ErrorCodes.NotFound,
                        $"Sessions not found: {string.Join(", ", missing)}");
                }
            }

            if (selected.Length == 0)
            {
                throw new BridgeOperationException(ErrorCodes.InvalidRequest, "There are no sessions to save.");
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!Utilities.WriteSessionArchive(path, selected, string.Empty, false))
            {
                throw new BridgeOperationException(ErrorCodes.Internal, "Fiddler could not save the SAZ archive.");
            }

            return new ArchiveResponse { Path = path, SessionCount = selected.Length };
        });
    }

    /// <summary>
    /// Imports sessions from a validated existing SAZ archive.
    /// </summary>
    /// <param name="request">The absolute archive path to import.</param>
    private static ArchiveResponse LoadSessions(LoadSessionsRequest request)
    {
        var path = ValidateArchivePath(request.Path, mustExist: true);
        return OnUi(() =>
        {
            var imported = Utilities.ReadSessionArchive(path, false) ?? Array.Empty<Session>();
            FiddlerApplication.UI.AddImportedSessions(imported);
            return new ArchiveResponse { Path = path, SessionCount = imported.Length };
        });
    }

    /// <summary>
    /// Queues one captured session for replay without waiting for the resulting transaction.
    /// </summary>
    /// <param name="request">The session ID and unconditional replay choice.</param>
    private static QueuedResponse ReplaySession(ReplaySessionRequest request)
    {
        return OnUi(() =>
        {
            var session = FindSession(request.SessionId);
            var baselineSessionId = GetLatestSessionId();
            FiddlerApplication.UI.actReissueSessions(new[] { session }, request.Unconditional);
            return new QueuedResponse
            {
                Accepted = true,
                Message = $"Session {session.id} was queued for replay.",
                BaselineSessionId = baselineSessionId,
                ExpectedMethod = session.RequestMethod,
                ExpectedUrl = session.fullUrl
            };
        });
    }

    /// <summary>
    /// Queues a validated composed request through Fiddler without waiting for completion.
    /// </summary>
    /// <param name="request">The composed HTTP request.</param>
    private static QueuedResponse SendRequest(SendRequestRequest request)
    {
        var rawRequest = BuildRawRequest(request);
        return OnUi(() =>
        {
            var baselineSessionId = GetLatestSessionId();
            FiddlerObject.utilIssueRequest(rawRequest);
            return new QueuedResponse
            {
                Accepted = true,
                Message = $"{request.Method.ToUpperInvariant()} {request.Url} was queued.",
                BaselineSessionId = baselineSessionId,
                ExpectedMethod = request.Method.ToUpperInvariant(),
                ExpectedUrl = request.Url
            };
        });
    }

    /// <summary>
    /// Compares stable metadata, exact ordered headers, and request and response body hashes.
    /// </summary>
    /// <param name="request">The two captured session IDs to compare.</param>
    private static SessionDiffResponse DiffSessions(DiffSessionsRequest request)
    {
        return OnUi(() =>
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

    /// <summary>
    /// Lists a stable page of WebSocket frame metadata without copying payload bytes.
    /// </summary>
    /// <param name="request">The tunnel session, zero-based offset, and result limit.</param>
    private static ListWebSocketMessagesResponse ListWebSocketMessages(ListWebSocketMessagesRequest request)
    {
        if (request.Offset < 0 || request.Limit < 1 || request.Limit > ProtocolConstants.MaxWebSocketMessageLimit)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"WebSocket offset must be non-negative and limit must be between 1 and {ProtocolConstants.MaxWebSocketMessageLimit}.");
        }

        return OnUi(() =>
        {
            var session = FindSession(request.SessionId);
            var messages = GetWebSocket(session).listMessages.ToArray();
            return new ListWebSocketMessagesResponse
            {
                SessionId = session.id,
                TotalMessages = messages.Length,
                Messages = messages.Skip(request.Offset).Take(request.Limit).Select(ToWebSocketSummary).ToList()
            };
        });
    }

    /// <summary>
    /// Copies one bounded byte range from a WebSocket frame payload.
    /// </summary>
    /// <param name="request">The session, message ID, payload offset, and bounded byte count.</param>
    private static WebSocketMessageChunk GetWebSocketMessage(GetWebSocketMessageRequest request)
    {
        if (request.Offset < 0 || request.Length < 1 || request.Length > ProtocolConstants.CliBodyChunkBytes)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"WebSocket payload offset must be non-negative and length must be between 1 and {ProtocolConstants.CliBodyChunkBytes}.");
        }

        return OnUi(() =>
        {
            var session = FindSession(request.SessionId);
            var message = GetWebSocket(session).listMessages.FirstOrDefault(candidate => candidate.ID == request.MessageId)
                ?? throw new BridgeOperationException(
                    ErrorCodes.NotFound,
                    $"WebSocket message {request.MessageId} was not found in session {request.SessionId}.");
            var payload = message.PayloadData ?? Array.Empty<byte>();
            if (request.Offset > payload.LongLength)
            {
                throw new BridgeOperationException(
                    ErrorCodes.InvalidRequest,
                    $"Payload offset {request.Offset} exceeds message length {payload.LongLength}.");
            }

            var count = (int)Math.Min(request.Length, payload.LongLength - request.Offset);
            var chunk = new byte[count];
            if (count > 0)
            {
                Buffer.BlockCopy(payload, checked((int)request.Offset), chunk, 0, count);
            }

            var summary = ToWebSocketSummary(message);
            return new WebSocketMessageChunk
            {
                SessionId = session.id,
                MessageId = message.ID,
                Direction = summary.Direction,
                Opcode = summary.Opcode,
                IsFinal = summary.IsFinal,
                Offset = request.Offset,
                BytesReturned = count,
                TotalBytes = payload.LongLength,
                EndOfMessage = request.Offset + count >= payload.LongLength,
                Base64Data = Convert.ToBase64String(chunk),
                TimestampUtc = summary.TimestampUtc
            };
        });
    }

    /// <summary>
    /// Converts raw-request validation failures into stable bridge operation errors.
    /// </summary>
    /// <param name="request">The composed request to validate and serialize.</param>
    private static string BuildRawRequest(SendRequestRequest request)
    {
        if (!RawRequestBuilder.TryBuild(request, out var rawRequest, out var errorMessage))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, errorMessage);
        }

        return rawRequest;
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

    private static int GetLatestSessionId()
    {
        var sessions = FiddlerApplication.UI.GetAllSessions();
        return sessions.Length == 0 ? 0 : sessions.Max(session => session.id);
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

    private static WebSocket GetWebSocket(Session session)
    {
        return session.__oTunnel as WebSocket
            ?? throw new BridgeOperationException(ErrorCodes.NotFound, $"Session {session.id} has no WebSocket messages.");
    }

    private static WebSocketMessageSummary ToWebSocketSummary(WebSocketMessage message)
    {
        var timestamp = message.IsOutbound ? message.Timers.dtBeginSend : message.Timers.dtDoneRead;
        return new WebSocketMessageSummary
        {
            MessageId = message.ID,
            Direction = message.IsOutbound ? WebSocketDirections.Sent : WebSocketDirections.Received,
            Opcode = message.FrameType.ToString().ToLowerInvariant(),
            IsFinal = message.IsFinalFrame,
            IsContinuation = message.FrameType == WebSocketFrameTypes.Continuation,
            WasAborted = message.WasAborted,
            CloseReason = message.FrameType == WebSocketFrameTypes.Close ? message.iCloseReason : (int?)null,
            PayloadLength = message.PayloadLength,
            TimestampUtc = timestamp.Year > 1900
                ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : null
        };
    }

    /// <summary>
    /// Records only completed session IDs in a bounded buffer and wakes matching wait operations.
    /// </summary>
    /// <param name="session">The session that reached Fiddler's completed state.</param>
    private void OnAfterSessionComplete(Session session)
    {
        lock (_completionLock)
        {
            _completedSessionIds.Enqueue(session.id);
            while (_completedSessionIds.Count > CompletedSessionBufferCapacity)
            {
                _completedSessionIds.Dequeue();
            }

            _completionSequence++;
            Monitor.PulseAll(_completionLock);
        }
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

    private static bool Contains(string? value, string expected)
    {
        return value != null && value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
    }

#pragma warning disable CS0618 // Fiddler Classic exposes the active capture flag through this obsolete member.
    private static bool IsProxyAttached(Proxy proxy)
    {
        return proxy.IsAttached;
    }
#pragma warning restore CS0618

    /// <summary>
    /// Normalizes a SAZ path and converts validation output into a bridge operation error.
    /// </summary>
    /// <param name="path">The user-supplied absolute archive path.</param>
    /// <param name="mustExist">Whether the archive file must already exist.</param>
    private static string ValidateArchivePath(string path, bool mustExist)
    {
        if (!ArchivePaths.TryValidate(path, mustExist, out var fullPath, out var errorCode, out var errorMessage))
        {
            throw new BridgeOperationException(errorCode, errorMessage);
        }

        return fullPath;
    }

    /// <summary>
    /// Deserializes an operation payload using the bridge's bounded serializer settings.
    /// </summary>
    /// <typeparam name="T">The expected operation payload type.</typeparam>
    /// <param name="request">The containing bridge request envelope.</param>
    private static T ReadPayload<T>(BridgeRequest request)
        where T : class, new()
    {
        if (string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            return new T();
        }

        return CreateSerializer().Deserialize<T>(request.PayloadJson)
            ?? throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The operation payload was empty.");
    }

    /// <summary>
    /// Creates a successful response envelope with a serialized payload.
    /// </summary>
    /// <param name="requestId">The request correlation ID.</param>
    /// <param name="payload">The operation result to serialize.</param>
    private static BridgeResponse Success(string requestId, object payload)
    {
        return new BridgeResponse
        {
            ProtocolVersion = ProtocolConstants.Version,
            RequestId = requestId,
            Success = true,
            PayloadJson = CreateSerializer().Serialize(payload)
        };
    }

    /// <summary>
    /// Creates a failed response envelope with a stable error code and message.
    /// </summary>
    /// <param name="requestId">The request correlation ID.</param>
    /// <param name="code">The stable bridge error code.</param>
    /// <param name="message">The actionable error message.</param>
    private static BridgeResponse Failure(string requestId, string code, string message)
    {
        return new BridgeResponse
        {
            ProtocolVersion = ProtocolConstants.Version,
            RequestId = requestId,
            Success = false,
            Error = new BridgeError { Code = code, Message = message }
        };
    }

    private static JavaScriptSerializer CreateSerializer()
    {
        return new JavaScriptSerializer
        {
            MaxJsonLength = ProtocolConstants.MaxFrameBytes,
            RecursionLimit = 64
        };
    }

    /// <summary>
    /// Executes an operation synchronously on Fiddler's UI thread when marshaling is required.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="action">The Fiddler API operation to execute.</param>
    private static T OnUi<T>(Func<T> action)
    {
        var ui = FiddlerApplication.UI
            ?? throw new BridgeOperationException(ErrorCodes.Unavailable, "Fiddler's user interface is not available.");

        if (ui.InvokeRequired)
        {
            return (T)ui.Invoke(action);
        }

        return action();
    }
}
