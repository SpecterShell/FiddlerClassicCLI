// Centralizes protocol versions, limits, operation names, and stable error codes.
namespace FiddlerClassicCLI.Protocol;

public static class ProtocolConstants
{
    public const int Version = 3;
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public const int McpMaxBodyChunkBytes = 64 * 1024;
    public const int CliBodyChunkBytes = 256 * 1024;
    public const int MaxComposeBodyBytes = 4 * 1024 * 1024;
    public const int DefaultSessionLimit = 100;
    public const int MaxSessionLimit = 1000;
    public const int MaxWaitMilliseconds = 60 * 1000;
    public const int DefaultWaitMilliseconds = 10 * 1000;
    public const int DefaultBodySearchBytes = 64 * 1024;
    public const int MaxBodySearchBytes = 1024 * 1024;
    public const int MaxWebSocketMessageLimit = 1000;
    public const int MaxAutoResponderRules = 5000;
    public const int MaxAutoResponderTextLength = 32 * 1024;
    public const int DefaultBreakpointHoldMilliseconds = 30 * 1000;
    public const int MaxBreakpointHoldMilliseconds = 5 * 60 * 1000;
    public const int MaxBreakpointArms = 128;
    public const int MaxPendingBreakpoints = 128;
}

public static class Operations
{
    public const string GetStatus = "status.get";
    public const string SetCapture = "capture.set";
    public const string ListSessions = "sessions.list";
    public const string WaitForSession = "sessions.wait";
    public const string GetSessionDetails = "sessions.details";
    public const string GetSessionBody = "sessions.body";
    public const string ClearSessions = "sessions.clear";
    public const string RemoveSessions = "sessions.remove";
    public const string SaveSessions = "sessions.save";
    public const string LoadSessions = "sessions.load";
    public const string ReplaySession = "sessions.replay";
    public const string SendRequest = "request.send";
    public const string DiffSessions = "sessions.diff";
    public const string ListWebSocketMessages = "sessions.websocket.list";
    public const string GetWebSocketMessage = "sessions.websocket.message";
    public const string GetAutoResponder = "autoresponder.get";
    public const string ConfigureAutoResponder = "autoresponder.configure";
    public const string ListAutoResponderRules = "autoresponder.rules.list";
    public const string AddAutoResponderRule = "autoresponder.rules.add";
    public const string UpdateAutoResponderRule = "autoresponder.rules.update";
    public const string MoveAutoResponderRule = "autoresponder.rules.move";
    public const string RemoveAutoResponderRule = "autoresponder.rules.remove";
    public const string ClearAutoResponderRules = "autoresponder.rules.clear";
    public const string SaveAutoResponderRules = "autoresponder.rules.save";
    public const string LoadAutoResponderRules = "autoresponder.rules.load";
    public const string ListBreakpointArms = "breakpoints.arms.list";
    public const string ArmBreakpoint = "breakpoints.arms.add";
    public const string DisarmBreakpoint = "breakpoints.arms.remove";
    public const string ListPendingBreakpoints = "breakpoints.pending.list";
    public const string WaitForBreakpoint = "breakpoints.pending.wait";
    public const string GetBreakpoint = "breakpoints.pending.get";
    public const string UpdateBreakpoint = "breakpoints.pending.update";
    public const string ResumeBreakpoint = "breakpoints.pending.resume";
    public const string AbortBreakpoint = "breakpoints.pending.abort";
}

public static class ErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string NotFound = "not_found";
    public const string ConfirmationRequired = "confirmation_required";
    public const string Conflict = "conflict";
    public const string Unavailable = "unavailable";
    public const string Timeout = "timeout";
    public const string Internal = "internal_error";
}
