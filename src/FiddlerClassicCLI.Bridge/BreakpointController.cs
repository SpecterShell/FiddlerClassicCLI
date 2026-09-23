// Arms, tracks, mutates, resumes, and aborts bounded Fiddler request and response breakpoints.
using System.Globalization;
using System.Reflection;
using Fiddler;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed class BreakpointController : IDisposable
{
    private const string RequestArmFlag = "x-fiddler-classic-cli-request-arm";
    private const string RequestHoldFlag = "x-fiddler-classic-cli-request-hold";
    private const string ResponseArmFlag = "x-fiddler-classic-cli-response-arm";
    private const string ResponseHoldFlag = "x-fiddler-classic-cli-response-hold";

    private static readonly MethodInfo AbortMethod = typeof(Session).GetMethod(
        "Abort",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null)
        ?? throw new MissingMethodException(typeof(Session).FullName, "Abort");

    private readonly object _gate = new object();
    private readonly List<ArmEntry> _arms = new List<ArmEntry>();
    private readonly Dictionary<string, PendingEntry> _pending = new Dictionary<string, PendingEntry>(StringComparer.Ordinal);
    private readonly Dictionary<int, EventHandler<StateChangeEventArgs>> _stateHandlers =
        new Dictionary<int, EventHandler<StateChangeEventArgs>>();
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private long _sequence;
    private bool _disposed;

    /// <summary>
    /// Subscribes to header availability and session-state transitions used to create controllable pauses.
    /// </summary>
    public BreakpointController()
    {
        FiddlerApplication.RequestHeadersAvailable += OnRequestHeadersAvailable;
        FiddlerApplication.ResponseHeadersAvailable += OnResponseHeadersAvailable;
        FiddlerApplication.AfterSessionComplete += OnAfterSessionComplete;
    }

    /// <summary>
    /// Lists breakpoint arms in creation order.
    /// </summary>
    public ListBreakpointArmsResponse ListArms()
    {
        lock (_gate)
        {
            return new ListBreakpointArmsResponse { Arms = _arms.Select(entry => entry.Dto).ToList() };
        }
    }

    /// <summary>
    /// Creates a bounded request or response breakpoint arm.
    /// </summary>
    /// <param name="request">The stage, filters, reuse mode, and automatic hold timeout.</param>
    public BreakpointArmMutationResponse Arm(ArmBreakpointRequest request)
    {
        ValidateArm(request);
        var dto = new BreakpointArmDto
        {
            ArmId = Guid.NewGuid().ToString("N"),
            Stage = NormalizeStage(request.Stage),
            Filter = CopyFilter(request.Filter),
            OneShot = request.OneShot,
            HoldMilliseconds = request.HoldMilliseconds,
            CreatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_arms.Count >= ProtocolConstants.MaxBreakpointArms)
            {
                throw new BridgeOperationException(
                    ErrorCodes.Conflict,
                    $"At most {ProtocolConstants.MaxBreakpointArms} breakpoint arms may be active.");
            }

            _arms.Add(new ArmEntry(dto));
        }

        return new BreakpointArmMutationResponse { Arm = dto };
    }

    /// <summary>
    /// Removes an arm without changing sessions that are already paused.
    /// </summary>
    /// <param name="request">The runtime arm ID to remove.</param>
    public BreakpointArmMutationResponse Disarm(DisarmBreakpointRequest request)
    {
        ValidateId(request.ArmId, "breakpoint arm");
        lock (_gate)
        {
            var entry = _arms.FirstOrDefault(candidate => string.Equals(candidate.Dto.ArmId, request.ArmId, StringComparison.Ordinal));
            if (entry == null)
            {
                throw new BridgeOperationException(ErrorCodes.NotFound, $"Breakpoint arm '{request.ArmId}' was not found.");
            }

            _arms.Remove(entry);
            return new BreakpointArmMutationResponse { Removed = true, Arm = entry.Dto };
        }
    }

    /// <summary>
    /// Lists currently paused sessions with optional stage and session filters.
    /// </summary>
    /// <param name="request">The optional stage and session ID filters.</param>
    public ListPendingBreakpointsResponse ListPending(ListPendingBreakpointsRequest request)
    {
        ValidatePendingFilters(request.Stage, request.SessionId);
        return FiddlerThread.Invoke(() =>
        {
            lock (_gate)
            {
                return new ListPendingBreakpointsResponse
                {
                    LatestSequence = _sequence,
                    Breakpoints = _pending.Values
                        .Where(entry => MatchesPending(entry, request.Stage, request.SessionId, afterSequence: -1))
                        .OrderBy(entry => entry.Sequence)
                        .Select(ToDto)
                        .ToList()
                };
            }
        });
    }

    /// <summary>
    /// Waits for the first currently paused breakpoint after an exclusive sequence cursor.
    /// </summary>
    /// <param name="request">The cursor, timeout, and optional stage and session filters.</param>
    public WaitForBreakpointResponse Wait(WaitForBreakpointRequest request)
    {
        if (request.AfterSequence < 0)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The breakpoint sequence cursor cannot be negative.");
        }

        if (request.TimeoutMilliseconds < 1 || request.TimeoutMilliseconds > ProtocolConstants.MaxWaitMilliseconds)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"Wait timeout must be between 1 and {ProtocolConstants.MaxWaitMilliseconds} milliseconds.");
        }

        ValidatePendingFilters(request.Stage, request.SessionId);
        var deadline = DateTime.UtcNow.AddMilliseconds(request.TimeoutMilliseconds);
        while (true)
        {
            var result = FiddlerThread.Invoke(() =>
            {
                lock (_gate)
                {
                    var match = _pending.Values
                        .Where(entry => MatchesPending(entry, request.Stage, request.SessionId, request.AfterSequence))
                        .OrderBy(entry => entry.Sequence)
                        .FirstOrDefault();
                    return match == null
                        ? new WaitForBreakpointResponse { Matched = false, LatestSequence = _sequence }
                        : new WaitForBreakpointResponse
                        {
                            Matched = true,
                            LatestSequence = _sequence,
                            Breakpoint = ToDto(match)
                        };
                }
            });
            if (result.Matched)
            {
                return result;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new BridgeOperationException(ErrorCodes.Unavailable, "The breakpoint controller is shutting down.");
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return new WaitForBreakpointResponse { Matched = false, LatestSequence = _sequence };
                }

                Monitor.Wait(_gate, remaining);
            }
        }
    }

    /// <summary>
    /// Gets one paused breakpoint and optionally copies exact headers.
    /// </summary>
    /// <param name="request">The breakpoint ID and explicit header inclusion choice.</param>
    public BreakpointDetailsResponse Get(GetBreakpointRequest request)
    {
        ValidateId(request.BreakpointId, "breakpoint");
        return FiddlerThread.Invoke(() =>
        {
            lock (_gate)
            {
                var entry = FindPending(request.BreakpointId);
                return ToDetails(entry, request.IncludeHeaders);
            }
        });
    }

    /// <summary>
    /// Mutates the stage-appropriate URL, method, status, headers, or body while a session is paused.
    /// </summary>
    /// <param name="request">The paused breakpoint ID and supplied replacement fields.</param>
    public BreakpointDetailsResponse Update(UpdateBreakpointRequest request)
    {
        ValidateUpdate(request, out var body);
        return FiddlerThread.Invoke(() =>
        {
            lock (_gate)
            {
                var entry = FindPending(request.BreakpointId);
                EnsureStillPaused(entry);
                ValidateStageMutation(entry, request);
                ApplyUpdate(entry, request, body);
                return ToDetails(entry, includeHeaders: true);
            }
        });
    }

    /// <summary>
    /// Resumes one paused session at its current request or response stage.
    /// </summary>
    /// <param name="request">The paused breakpoint ID.</param>
    public BreakpointActionResponse Resume(BreakpointActionRequest request)
    {
        return Complete(request.BreakpointId, abort: false);
    }

    /// <summary>
    /// Aborts one paused session after explicit destructive confirmation.
    /// </summary>
    /// <param name="request">The paused breakpoint ID and confirmation.</param>
    public BreakpointActionResponse Abort(BreakpointActionRequest request)
    {
        if (!request.Confirm)
        {
            throw new BridgeOperationException(ErrorCodes.ConfirmationRequired, "Aborting a paused session requires confirmation.");
        }

        return Complete(request.BreakpointId, abort: true);
    }

    /// <summary>
    /// Unsubscribes events, cancels timeout tasks, and resumes managed pauses during extension shutdown.
    /// </summary>
    public void Dispose()
    {
        List<PendingEntry> managed;
        KeyValuePair<int, EventHandler<StateChangeEventArgs>>[] handlers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            managed = _pending.Values.Where(entry => entry.IsManaged).ToList();
            handlers = _stateHandlers.ToArray();
            _arms.Clear();
            _pending.Clear();
            _stateHandlers.Clear();
            Monitor.PulseAll(_gate);
        }

        FiddlerApplication.RequestHeadersAvailable -= OnRequestHeadersAvailable;
        FiddlerApplication.ResponseHeadersAvailable -= OnResponseHeadersAvailable;
        FiddlerApplication.AfterSessionComplete -= OnAfterSessionComplete;
        try
        {
            FiddlerThread.Invoke(() =>
            {
                var sessions = FiddlerApplication.UI.GetAllSessions();
                foreach (var pair in handlers)
                {
                    var session = sessions.FirstOrDefault(candidate => candidate.id == pair.Key);
                    if (session != null)
                    {
                        session.OnStateChanged -= pair.Value;
                    }
                }

                foreach (var entry in managed)
                {
                    try
                    {
                        entry.Session.ThreadResume();
                    }
                    catch (Exception exception)
                    {
                        FiddlerApplication.Log.LogString($"[FiddlerClassicCLI] Could not resume session {entry.Session.id} during unload: {exception.Message}");
                    }
                }
            });
        }
        catch (Exception exception)
        {
            FiddlerApplication.Log.LogString($"[FiddlerClassicCLI] Breakpoint shutdown cleanup failed: {exception.Message}");
        }

        _lifetime.Dispose();
    }

    private void OnRequestHeadersAvailable(Session session)
    {
        AttachStateTracking(session);
        TryArmSession(session, BreakpointStages.Request);
    }

    private void OnResponseHeadersAvailable(Session session)
    {
        AttachStateTracking(session);
        TryArmSession(session, BreakpointStages.Response);
    }

    private void OnAfterSessionComplete(Session session)
    {
        EventHandler<StateChangeEventArgs>? handler = null;
        lock (_gate)
        {
            RemovePendingForSession(session.id);
            if (_stateHandlers.TryGetValue(session.id, out handler))
            {
                _stateHandlers.Remove(session.id);
            }
        }

        if (handler != null)
        {
            session.OnStateChanged -= handler;
        }
    }

    private void AttachStateTracking(Session session)
    {
        lock (_gate)
        {
            if (_disposed || _stateHandlers.ContainsKey(session.id))
            {
                return;
            }

            EventHandler<StateChangeEventArgs> handler = (_, arguments) => OnStateChanged(session, arguments);
            _stateHandlers.Add(session.id, handler);
            session.OnStateChanged += handler;
        }
    }

    private void TryArmSession(Session session, string stage)
    {
        ArmEntry? match;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            match = _arms.FirstOrDefault(entry =>
                string.Equals(entry.Dto.Stage, stage, StringComparison.Ordinal)
                && MatchesSession(session, stage, entry.Dto.Filter));
            if (match == null)
            {
                return;
            }

            if (match.Dto.OneShot)
            {
                _arms.Remove(match);
            }
        }

        if (string.Equals(stage, BreakpointStages.Request, StringComparison.Ordinal))
        {
            session[RequestArmFlag] = match.Dto.ArmId;
            session[RequestHoldFlag] = match.Dto.HoldMilliseconds.ToString(CultureInfo.InvariantCulture);
            session["x-breakrequest"] = "FiddlerClassicCLI";
        }
        else
        {
            session[ResponseArmFlag] = match.Dto.ArmId;
            session[ResponseHoldFlag] = match.Dto.HoldMilliseconds.ToString(CultureInfo.InvariantCulture);
            session["x-breakresponse"] = "FiddlerClassicCLI";
        }
    }

    private void OnStateChanged(Session session, StateChangeEventArgs arguments)
    {
        if (arguments.newState == SessionStates.HandTamperRequest)
        {
            AddPending(session, BreakpointStages.Request);
        }
        else if (arguments.newState == SessionStates.HandTamperResponse)
        {
            AddPending(session, BreakpointStages.Response);
        }
        else
        {
            lock (_gate)
            {
                if (arguments.oldState == SessionStates.HandTamperRequest)
                {
                    RemovePendingForStage(session.id, BreakpointStages.Request);
                }

                if (arguments.oldState == SessionStates.HandTamperResponse)
                {
                    RemovePendingForStage(session.id, BreakpointStages.Response);
                }
            }
        }
    }

    private void AddPending(Session session, string stage)
    {
        PendingEntry? entry = null;
        lock (_gate)
        {
            if (_disposed || _pending.Values.Any(candidate => candidate.Session.id == session.id && candidate.Stage == stage))
            {
                return;
            }

            var armFlag = stage == BreakpointStages.Request ? RequestArmFlag : ResponseArmFlag;
            var holdFlag = stage == BreakpointStages.Request ? RequestHoldFlag : ResponseHoldFlag;
            var armId = EmptyToNull(session[armFlag]);
            var managed = armId != null;
            if (_pending.Count >= ProtocolConstants.MaxPendingBreakpoints)
            {
                if (managed)
                {
                    session.ThreadResume();
                }

                FiddlerApplication.Log.LogString(
                    $"[FiddlerClassicCLI] Pending breakpoint limit reached for session {session.id}. "
                    + (managed ? "the managed pause was resumed." : "the manual pause remains available in Fiddler."));
                return;
            }

            var hold = managed && int.TryParse(session[holdFlag], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
            var pausedAt = DateTime.UtcNow;
            entry = new PendingEntry
            {
                BreakpointId = Guid.NewGuid().ToString("N"),
                Sequence = ++_sequence,
                Session = session,
                Stage = stage,
                ArmId = armId,
                IsManaged = managed,
                PausedAtUtc = pausedAt,
                ExpiresAtUtc = managed ? pausedAt.AddMilliseconds(hold) : (DateTime?)null
            };
            _pending.Add(entry.BreakpointId, entry);
            Monitor.PulseAll(_gate);
        }

        if (entry.IsManaged && entry.ExpiresAtUtc.HasValue)
        {
            _ = ResumeAfterTimeout(entry.BreakpointId, entry.ExpiresAtUtc.Value);
        }
    }

    private async Task ResumeAfterTimeout(string breakpointId, DateTime expiresAtUtc)
    {
        try
        {
            var delay = expiresAtUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
            }

            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            FiddlerThread.Invoke(() =>
            {
                PendingEntry? entry;
                lock (_gate)
                {
                    if (!_pending.TryGetValue(breakpointId, out entry) || !entry.IsManaged)
                    {
                        return;
                    }

                    _pending.Remove(breakpointId);
                    Monitor.PulseAll(_gate);
                }

                if (IsExpectedPause(entry))
                {
                    entry.Session.ThreadResume();
                    FiddlerApplication.Log.LogString(
                        $"[FiddlerClassicCLI] Breakpoint {breakpointId} on session {entry.Session.id} resumed after its hold timeout.");
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FiddlerApplication.Log.LogString($"[FiddlerClassicCLI] Breakpoint timeout failed: {exception}");
        }
    }

    private BreakpointActionResponse Complete(string breakpointId, bool abort)
    {
        ValidateId(breakpointId, "breakpoint");
        return FiddlerThread.Invoke(() =>
        {
            PendingEntry entry;
            lock (_gate)
            {
                entry = FindPending(breakpointId);
                EnsureStillPaused(entry);
                _pending.Remove(breakpointId);
                Monitor.PulseAll(_gate);
            }

            if (abort)
            {
                AbortMethod.Invoke(entry.Session, parameters: null);
            }
            else
            {
                entry.Session.ThreadResume();
            }

            return new BreakpointActionResponse
            {
                BreakpointId = breakpointId,
                SessionId = entry.Session.id,
                Action = abort ? "aborted" : "resumed"
            };
        });
    }

    private static void ApplyUpdate(PendingEntry entry, UpdateBreakpointRequest request, byte[]? body)
    {
        var requestStage = string.Equals(entry.Stage, BreakpointStages.Request, StringComparison.Ordinal);
        if (requestStage)
        {
            if (request.Method != null)
            {
                entry.Session.RequestMethod = request.Method;
            }

            if (request.Url != null)
            {
                entry.Session.fullUrl = request.Url;
            }

            ApplyHeaders(entry.Session.RequestHeaders, request.SetHeaders, request.RemoveHeaders);
            if (body != null)
            {
                entry.Session.RequestBody = body;
            }
        }
        else
        {
            if (request.StatusCode.HasValue)
            {
                var description = request.StatusDescription ?? entry.Session.ResponseHeaders.StatusDescription ?? string.Empty;
                entry.Session.ResponseHeaders.SetStatus(request.StatusCode.Value, description);
            }
            else if (request.StatusDescription != null)
            {
                entry.Session.ResponseHeaders.SetStatus(entry.Session.responseCode, request.StatusDescription);
            }

            ApplyHeaders(entry.Session.ResponseHeaders, request.SetHeaders, request.RemoveHeaders);
            if (body != null)
            {
                entry.Session.ResponseBody = body;
            }
        }
    }

    /// <summary>
    /// Rejects request-only fields on response pauses and response-only fields on request pauses.
    /// </summary>
    /// <param name="entry">The currently paused session and stage.</param>
    /// <param name="request">The validated mutation fields.</param>
    private static void ValidateStageMutation(PendingEntry entry, UpdateBreakpointRequest request)
    {
        if (entry.Stage == BreakpointStages.Request
            && (request.StatusCode.HasValue || request.StatusDescription != null))
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                "Status code and description can be changed only at a response breakpoint.");
        }

        if (entry.Stage == BreakpointStages.Response
            && (request.Method != null || request.Url != null))
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                "Method and URL can be changed only at a request breakpoint.");
        }
    }

    private static void ApplyHeaders(HTTPHeaders headers, IEnumerable<HeaderDto> setHeaders, IEnumerable<string> removeHeaders)
    {
        foreach (var name in removeHeaders)
        {
            headers.Remove(name);
        }

        foreach (var header in setHeaders)
        {
            headers[header.Name] = header.Value;
        }
    }

    private static bool MatchesSession(Session session, string stage, BreakpointFilterDto filter)
    {
        if (!EqualsIgnoreCase(session.RequestMethod, filter.Method)
            || !ContainsIgnoreCase(session.hostname, filter.Host)
            || !ContainsIgnoreCase(session.fullUrl, filter.UrlContains)
            || !ContainsIgnoreCase(session.LocalProcess, filter.Process))
        {
            return false;
        }

        HTTPHeaders? headers;
        if (stage == BreakpointStages.Response)
        {
            if (filter.StatusCode.HasValue && session.responseCode != filter.StatusCode.Value)
            {
                return false;
            }

            if (!ContainsIgnoreCase(session.ResponseHeaders?["Content-Type"], filter.ContentType))
            {
                return false;
            }

            headers = session.ResponseHeaders;
        }
        else
        {
            headers = session.RequestHeaders;
        }

        if (filter.HeaderName != null)
        {
            var value = headers?[filter.HeaderName];
            return value != null && ContainsIgnoreCase(value, filter.HeaderValue);
        }

        return true;
    }

    private static bool MatchesPending(PendingEntry entry, string? stage, int? sessionId, long afterSequence)
    {
        return entry.Sequence > afterSequence
            && (stage == null || string.Equals(entry.Stage, stage, StringComparison.OrdinalIgnoreCase))
            && (!sessionId.HasValue || entry.Session.id == sessionId.Value)
            && IsExpectedPause(entry);
    }

    private static PendingBreakpointDto ToDto(PendingEntry entry)
    {
        return new PendingBreakpointDto
        {
            BreakpointId = entry.BreakpointId,
            Sequence = entry.Sequence,
            SessionId = entry.Session.id,
            Stage = entry.Stage,
            State = entry.Session.state.ToString(),
            ArmId = entry.ArmId,
            IsManaged = entry.IsManaged,
            PausedAtUtc = entry.PausedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ExpiresAtUtc = entry.ExpiresAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            Method = entry.Session.RequestMethod ?? string.Empty,
            Url = entry.Session.fullUrl ?? string.Empty,
            StatusCode = entry.Session.responseCode > 0 ? entry.Session.responseCode : (int?)null,
            ContentType = entry.Session.ResponseHeaders?["Content-Type"],
            Process = entry.Session.LocalProcess
        };
    }

    private static BreakpointDetailsResponse ToDetails(PendingEntry entry, bool includeHeaders)
    {
        return new BreakpointDetailsResponse
        {
            Breakpoint = ToDto(entry),
            RequestHeaders = includeHeaders ? CopyHeaders(entry.Session.RequestHeaders) : null,
            ResponseHeaders = includeHeaders ? CopyHeaders(entry.Session.ResponseHeaders) : null,
            RequestBodyBytes = entry.Session.requestBodyBytes?.LongLength ?? 0,
            ResponseBodyBytes = entry.Session.responseBodyBytes?.LongLength ?? 0
        };
    }

    private static List<HeaderDto> CopyHeaders(HTTPHeaders? headers)
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

    private PendingEntry FindPending(string breakpointId)
    {
        return _pending.TryGetValue(breakpointId, out var entry)
            ? entry
            : throw new BridgeOperationException(ErrorCodes.NotFound, $"Pending breakpoint '{breakpointId}' was not found.");
    }

    private static void EnsureStillPaused(PendingEntry entry)
    {
        if (!IsExpectedPause(entry))
        {
            throw new BridgeOperationException(ErrorCodes.Conflict, "The session is no longer paused at this breakpoint.");
        }
    }

    private static bool IsExpectedPause(PendingEntry entry)
    {
        return entry.Stage == BreakpointStages.Request
            ? entry.Session.state == SessionStates.HandTamperRequest
            : entry.Session.state == SessionStates.HandTamperResponse;
    }

    private void RemovePendingForSession(int sessionId)
    {
        foreach (var id in _pending.Where(pair => pair.Value.Session.id == sessionId).Select(pair => pair.Key).ToArray())
        {
            _pending.Remove(id);
        }

        Monitor.PulseAll(_gate);
    }

    private void RemovePendingForStage(int sessionId, string stage)
    {
        foreach (var id in _pending
            .Where(pair => pair.Value.Session.id == sessionId && pair.Value.Stage == stage)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _pending.Remove(id);
        }

        Monitor.PulseAll(_gate);
    }

    private static void ValidateArm(ArmBreakpointRequest request)
    {
        request.Filter = request.Filter ?? new BreakpointFilterDto();
        var stage = NormalizeStage(request.Stage);
        if (request.HoldMilliseconds < 1 || request.HoldMilliseconds > ProtocolConstants.MaxBreakpointHoldMilliseconds)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"Breakpoint hold must be between 1 and {ProtocolConstants.MaxBreakpointHoldMilliseconds} milliseconds.");
        }

        if (stage == BreakpointStages.Request
            && (request.Filter.StatusCode.HasValue || request.Filter.ContentType != null))
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                "Status and content-type filters are available only for response breakpoints.");
        }

        if (request.Filter.Method != null && !HttpInputValidation.IsMethod(request.Filter.Method))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The breakpoint method filter is invalid.");
        }

        if (request.Filter.StatusCode is < 100 or > 999)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The breakpoint status code must be between 100 and 999.");
        }

        if (request.Filter.HeaderName == null && request.Filter.HeaderValue != null)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "A header value filter requires a header name.");
        }

        if (request.Filter.HeaderName != null && !HttpInputValidation.IsHeaderName(request.Filter.HeaderName))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The breakpoint header name is invalid.");
        }

        foreach (var value in new[]
        {
            request.Filter.Host,
            request.Filter.UrlContains,
            request.Filter.ContentType,
            request.Filter.Process,
            request.Filter.HeaderValue
        })
        {
            if (value != null
                && (value.Length > ProtocolConstants.MaxAutoResponderTextLength
                    || value.IndexOfAny(new[] { '\r', '\n' }) >= 0))
            {
                throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Breakpoint filters must be bounded single-line text.");
            }
        }
    }

    private static void ValidateUpdate(UpdateBreakpointRequest request, out byte[]? body)
    {
        ValidateId(request.BreakpointId, "breakpoint");
        if (request.Method != null && !HttpInputValidation.IsMethod(request.Method))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The replacement HTTP method is invalid.");
        }

        if (request.Url != null && !HttpInputValidation.IsHttpUrl(request.Url))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The replacement URL must be absolute HTTP or HTTPS.");
        }

        if (request.StatusCode is < 100 or > 999)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The replacement status code must be between 100 and 999.");
        }

        if (request.StatusDescription != null
            && (request.StatusDescription.Length > 1024 || request.StatusDescription.IndexOfAny(new[] { '\r', '\n' }) >= 0))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The status description must be bounded single-line text.");
        }

        request.SetHeaders = request.SetHeaders ?? new List<HeaderDto>();
        request.RemoveHeaders = request.RemoveHeaders ?? Array.Empty<string>();
        foreach (var header in request.SetHeaders)
        {
            if (!HttpInputValidation.TryValidateHeader(header, out var error))
            {
                throw new BridgeOperationException(ErrorCodes.InvalidRequest, error);
            }
        }

        foreach (var name in request.RemoveHeaders)
        {
            if (!HttpInputValidation.IsHeaderName(name))
            {
                throw new BridgeOperationException(ErrorCodes.InvalidRequest, $"Header name '{name}' is invalid.");
            }
        }

        if (!HttpInputValidation.TryDecodeBody(request.BodyBase64, out body, out var bodyError))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, bodyError);
        }

        if (request.Method == null
            && request.Url == null
            && !request.StatusCode.HasValue
            && request.StatusDescription == null
            && request.SetHeaders.Count == 0
            && request.RemoveHeaders.Length == 0
            && request.BodyBase64 == null)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "At least one breakpoint mutation is required.");
        }
    }

    private static void ValidatePendingFilters(string? stage, int? sessionId)
    {
        if (stage != null)
        {
            NormalizeStage(stage);
        }

        if (sessionId <= 0)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Session ID must be positive.");
        }
    }

    private static string NormalizeStage(string stage)
    {
        if (string.Equals(stage, BreakpointStages.Request, StringComparison.OrdinalIgnoreCase))
        {
            return BreakpointStages.Request;
        }

        if (string.Equals(stage, BreakpointStages.Response, StringComparison.OrdinalIgnoreCase))
        {
            return BreakpointStages.Response;
        }

        throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Breakpoint stage must be 'request' or 'response'.");
    }

    private static BreakpointFilterDto CopyFilter(BreakpointFilterDto filter)
    {
        return new BreakpointFilterDto
        {
            Method = filter.Method,
            Host = filter.Host,
            UrlContains = filter.UrlContains,
            StatusCode = filter.StatusCode,
            ContentType = filter.ContentType,
            Process = filter.Process,
            HeaderName = filter.HeaderName,
            HeaderValue = filter.HeaderValue
        };
    }

    private static void ValidateId(string id, string label)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, $"The {label} ID is invalid.");
        }
    }

    private static bool EqualsIgnoreCase(string? actual, string? expected)
    {
        return expected == null || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIgnoreCase(string? actual, string? expected)
    {
        return expected == null || (actual != null && actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new BridgeOperationException(ErrorCodes.Unavailable, "The breakpoint controller is shutting down.");
        }
    }

    private sealed class ArmEntry
    {
        public ArmEntry(BreakpointArmDto dto)
        {
            Dto = dto;
        }

        public BreakpointArmDto Dto { get; }
    }

    private sealed class PendingEntry
    {
        public string BreakpointId { get; set; } = string.Empty;
        public long Sequence { get; set; }
        public Session Session { get; set; } = null!;
        public string Stage { get; set; } = string.Empty;
        public string? ArmId { get; set; }
        public bool IsManaged { get; set; }
        public DateTime PausedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
    }
}
