// Dispatches versioned bridge operations onto Fiddler Classic's UI-owned session state.
using System.Web.Script.Serialization;
using Fiddler;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge;

internal sealed partial class BridgeDispatcher : IDisposable
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
        return FiddlerThread.Invoke(() =>
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
        return FiddlerThread.Invoke(() =>
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

#pragma warning disable CS0618 // Fiddler Classic exposes the active capture flag through this obsolete member.
    private static bool IsProxyAttached(Proxy proxy)
    {
        return proxy.IsAttached;
    }
#pragma warning restore CS0618

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
}
