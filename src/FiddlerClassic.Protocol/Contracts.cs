// Defines the versioned JSON contracts shared by the host and Fiddler extension.
namespace FiddlerClassic.Protocol;

public sealed class BridgeRequest
{
    public int ProtocolVersion { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
}

public sealed class BridgeResponse
{
    public int ProtocolVersion { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public BridgeError? Error { get; set; }
}

public sealed class BridgeError
{
    public string Code { get; set; } = ErrorCodes.Internal;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
}

public sealed class EmptyRequest
{
}

public sealed class StatusResponse
{
    public bool FiddlerInstalled { get; set; }
    public string? FiddlerPath { get; set; }
    public bool FiddlerRunning { get; set; }
    public int? FiddlerProcessId { get; set; }
    public string? FiddlerVersion { get; set; }
    public bool BridgeInstalled { get; set; }
    public bool BridgeConnected { get; set; }
    public string? BridgeVersion { get; set; }
    public bool IsProxyAttached { get; set; }
    public bool IsListening { get; set; }
    public int? ListenPort { get; set; }
    public bool IsHttpsDecryptionEnabled { get; set; }
    public int SessionCount { get; set; }
    public int CompletedSessionCount { get; set; }
}

public sealed class SetCaptureRequest
{
    public bool Enabled { get; set; }
}

public sealed class CaptureResponse
{
    public bool IsProxyAttached { get; set; }
}

public sealed class ListSessionsRequest
{
    public int? MinId { get; set; }
    public int? MaxId { get; set; }
    public string? Method { get; set; }
    public string? Host { get; set; }
    public string? UrlContains { get; set; }
    public int? StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? Process { get; set; }
    public string? HeaderName { get; set; }
    public string? HeaderValue { get; set; }
    public double? MinDurationMilliseconds { get; set; }
    public double? MaxDurationMilliseconds { get; set; }
    public string? Protocol { get; set; }
    public long? MinBodyBytes { get; set; }
    public long? MaxBodyBytes { get; set; }
    public bool? IsError { get; set; }
    public string? BodyContains { get; set; }
    public string BodyDirection { get; set; } = BodyDirections.Response;
    public int BodySearchBytes { get; set; } = ProtocolConstants.DefaultBodySearchBytes;
    public int Limit { get; set; } = ProtocolConstants.DefaultSessionLimit;
    public bool NewestFirst { get; set; } = true;
}

public sealed class WaitForSessionRequest
{
    public int AfterId { get; set; }
    public int TimeoutMilliseconds { get; set; } = ProtocolConstants.DefaultWaitMilliseconds;
    public ListSessionsRequest Filters { get; set; } = new ListSessionsRequest();
}

public sealed class WaitForSessionResponse
{
    public bool Matched { get; set; }
    public SessionSummary? Session { get; set; }
    public int LatestSessionId { get; set; }
}

public sealed class ListSessionsResponse
{
    public int TotalMatched { get; set; }
    public List<SessionSummary> Sessions { get; set; } = new List<SessionSummary>();
}

public sealed class SessionSummary
{
    public int Id { get; set; }
    public string State { get; set; } = string.Empty;
    public bool IsComplete { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string PathAndQuery { get; set; } = string.Empty;
    public string Scheme { get; set; } = string.Empty;
    public string? Protocol { get; set; }
    public int? StatusCode { get; set; }
    public string? StatusDescription { get; set; }
    public string? ContentType { get; set; }
    public string? Process { get; set; }
    public int ProcessId { get; set; }
    public string? ClientIp { get; set; }
    public string? ServerIp { get; set; }
    public long RequestBodyBytes { get; set; }
    public long ResponseBodyBytes { get; set; }
    public string? StartedAtUtc { get; set; }
    public double? DurationMilliseconds { get; set; }
    public bool IsError { get; set; }
    public bool HasWebSocketMessages { get; set; }
}

public sealed class GetSessionDetailsRequest
{
    public int SessionId { get; set; }
    public bool IncludeHeaders { get; set; }
}

public sealed class SessionDetails
{
    public SessionSummary Summary { get; set; } = new SessionSummary();
    public List<HeaderDto>? RequestHeaders { get; set; }
    public List<HeaderDto>? ResponseHeaders { get; set; }
}

public sealed class HeaderDto
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class GetSessionBodyRequest
{
    public int SessionId { get; set; }
    public string Direction { get; set; } = BodyDirections.Response;
    public long Offset { get; set; }
    public int Length { get; set; } = ProtocolConstants.McpMaxBodyChunkBytes;
}

public static class BodyDirections
{
    public const string Request = "request";
    public const string Response = "response";
}

public sealed class SessionBodyChunk
{
    public int SessionId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public long Offset { get; set; }
    public int BytesReturned { get; set; }
    public long TotalBytes { get; set; }
    public bool EndOfBody { get; set; }
    public string Base64Data { get; set; } = string.Empty;
    public string? ContentType { get; set; }
}

public sealed class ClearSessionsRequest
{
    public bool Confirm { get; set; }
}

public sealed class ClearSessionsResponse
{
    public int RemovedCount { get; set; }
}

public sealed class RemoveSessionsRequest
{
    public int[] SessionIds { get; set; } = Array.Empty<int>();
    public bool Confirm { get; set; }
}

public sealed class RemoveSessionsResponse
{
    public int RemovedCount { get; set; }
    public int[] RemovedSessionIds { get; set; } = Array.Empty<int>();
}

public sealed class SaveSessionsRequest
{
    public string Path { get; set; } = string.Empty;
    public int[]? SessionIds { get; set; }
    public bool Overwrite { get; set; }
    public bool ConfirmOverwrite { get; set; }
}

public sealed class LoadSessionsRequest
{
    public string Path { get; set; } = string.Empty;
}

public sealed class ArchiveResponse
{
    public string Path { get; set; } = string.Empty;
    public int SessionCount { get; set; }
}

public sealed class ReplaySessionRequest
{
    public int SessionId { get; set; }
    public bool Unconditional { get; set; }
}

public sealed class SendRequestRequest
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = string.Empty;
    public List<HeaderDto> Headers { get; set; } = new List<HeaderDto>();
    public string? BodyBase64 { get; set; }
}

public sealed class QueuedResponse
{
    public bool Accepted { get; set; }
    public string Message { get; set; } = string.Empty;
    public int BaselineSessionId { get; set; }
    public string? ExpectedMethod { get; set; }
    public string? ExpectedUrl { get; set; }
}

public sealed class DiffSessionsRequest
{
    public int LeftSessionId { get; set; }
    public int RightSessionId { get; set; }
}

public sealed class SessionDiffResponse
{
    public int LeftSessionId { get; set; }
    public int RightSessionId { get; set; }
    public List<SessionDiffEntry> Differences { get; set; } = new List<SessionDiffEntry>();
}

public sealed class SessionDiffEntry
{
    public string Area { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string[] LeftValues { get; set; } = Array.Empty<string>();
    public string[] RightValues { get; set; } = Array.Empty<string>();
}

public sealed class ListWebSocketMessagesRequest
{
    public int SessionId { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = ProtocolConstants.DefaultSessionLimit;
}

public sealed class ListWebSocketMessagesResponse
{
    public int SessionId { get; set; }
    public int TotalMessages { get; set; }
    public List<WebSocketMessageSummary> Messages { get; set; } = new List<WebSocketMessageSummary>();
}

public sealed class WebSocketMessageSummary
{
    public int MessageId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Opcode { get; set; } = string.Empty;
    public bool IsFinal { get; set; }
    public bool IsContinuation { get; set; }
    public bool WasAborted { get; set; }
    public int? CloseReason { get; set; }
    public int PayloadLength { get; set; }
    public string? TimestampUtc { get; set; }
}

public sealed class GetWebSocketMessageRequest
{
    public int SessionId { get; set; }
    public int MessageId { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; } = ProtocolConstants.McpMaxBodyChunkBytes;
}

public sealed class WebSocketMessageChunk
{
    public int SessionId { get; set; }
    public int MessageId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Opcode { get; set; } = string.Empty;
    public bool IsFinal { get; set; }
    public long Offset { get; set; }
    public int BytesReturned { get; set; }
    public long TotalBytes { get; set; }
    public bool EndOfMessage { get; set; }
    public string Base64Data { get; set; } = string.Empty;
    public string? TimestampUtc { get; set; }
}

public static class WebSocketDirections
{
    public const string Sent = "sent";
    public const string Received = "received";
}

public sealed class AutoResponderStatusResponse
{
    public bool IsEnabled { get; set; }
    public bool PermitFallthrough { get; set; }
    public bool AcceptAllConnects { get; set; }
    public bool UseLatency { get; set; }
    public bool IsRuleListDirty { get; set; }
    public int RuleCount { get; set; }
}

public sealed class ConfigureAutoResponderRequest
{
    public bool? IsEnabled { get; set; }
    public bool? PermitFallthrough { get; set; }
    public bool? AcceptAllConnects { get; set; }
    public bool? UseLatency { get; set; }
}

public sealed class ListAutoResponderRulesResponse
{
    public List<AutoResponderRuleDto> Rules { get; set; } = new List<AutoResponderRuleDto>();
}

public sealed class AutoResponderRuleDto
{
    public string RuleId { get; set; } = string.Empty;
    public int Index { get; set; }
    public string Match { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? Comment { get; set; }
    public bool DisableOnMatch { get; set; }
    public int LatencyMilliseconds { get; set; }
    public bool HasImportedResponse { get; set; }
}

public sealed class AddAutoResponderRuleRequest
{
    public string Match { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string? Comment { get; set; }
    public bool DisableOnMatch { get; set; }
    public int LatencyMilliseconds { get; set; }
}

public sealed class UpdateAutoResponderRuleRequest
{
    public string RuleId { get; set; } = string.Empty;
    public string? Match { get; set; }
    public string? Action { get; set; }
    public bool? IsEnabled { get; set; }
    public string? Comment { get; set; }
    public bool SetComment { get; set; }
    public bool? DisableOnMatch { get; set; }
    public int? LatencyMilliseconds { get; set; }
}

public sealed class MoveAutoResponderRuleRequest
{
    public string RuleId { get; set; } = string.Empty;
    public int Index { get; set; }
}

public sealed class RemoveAutoResponderRuleRequest
{
    public string RuleId { get; set; } = string.Empty;
    public bool Confirm { get; set; }
}

public sealed class ClearAutoResponderRulesRequest
{
    public bool Confirm { get; set; }
}

public sealed class AutoResponderMutationResponse
{
    public int AffectedCount { get; set; }
    public AutoResponderRuleDto? Rule { get; set; }
}

public sealed class SaveAutoResponderRulesRequest
{
    public string Path { get; set; } = string.Empty;
    public bool Overwrite { get; set; }
    public bool ConfirmOverwrite { get; set; }
}

public sealed class LoadAutoResponderRulesRequest
{
    public string Path { get; set; } = string.Empty;
    public bool Replace { get; set; }
    public bool ConfirmReplace { get; set; }
}

public sealed class AutoResponderRulesFileResponse
{
    public string Path { get; set; } = string.Empty;
    public int RuleCount { get; set; }
}

public static class BreakpointStages
{
    public const string Request = "request";
    public const string Response = "response";
}

public sealed class BreakpointFilterDto
{
    public string? Method { get; set; }
    public string? Host { get; set; }
    public string? UrlContains { get; set; }
    public int? StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? Process { get; set; }
    public string? HeaderName { get; set; }
    public string? HeaderValue { get; set; }
}

public sealed class ArmBreakpointRequest
{
    public string Stage { get; set; } = BreakpointStages.Request;
    public BreakpointFilterDto Filter { get; set; } = new BreakpointFilterDto();
    public bool OneShot { get; set; } = true;
    public int HoldMilliseconds { get; set; } = ProtocolConstants.DefaultBreakpointHoldMilliseconds;
}

public sealed class BreakpointArmDto
{
    public string ArmId { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public BreakpointFilterDto Filter { get; set; } = new BreakpointFilterDto();
    public bool OneShot { get; set; }
    public int HoldMilliseconds { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;
}

public sealed class ListBreakpointArmsResponse
{
    public List<BreakpointArmDto> Arms { get; set; } = new List<BreakpointArmDto>();
}

public sealed class DisarmBreakpointRequest
{
    public string ArmId { get; set; } = string.Empty;
}

public sealed class BreakpointArmMutationResponse
{
    public bool Removed { get; set; }
    public BreakpointArmDto? Arm { get; set; }
}

public sealed class ListPendingBreakpointsRequest
{
    public string? Stage { get; set; }
    public int? SessionId { get; set; }
}

public sealed class ListPendingBreakpointsResponse
{
    public long LatestSequence { get; set; }
    public List<PendingBreakpointDto> Breakpoints { get; set; } = new List<PendingBreakpointDto>();
}

public sealed class PendingBreakpointDto
{
    public string BreakpointId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public int SessionId { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? ArmId { get; set; }
    public bool IsManaged { get; set; }
    public string PausedAtUtc { get; set; } = string.Empty;
    public string? ExpiresAtUtc { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? Process { get; set; }
}

public sealed class WaitForBreakpointRequest
{
    public long AfterSequence { get; set; }
    public int TimeoutMilliseconds { get; set; } = ProtocolConstants.DefaultWaitMilliseconds;
    public string? Stage { get; set; }
    public int? SessionId { get; set; }
}

public sealed class WaitForBreakpointResponse
{
    public bool Matched { get; set; }
    public long LatestSequence { get; set; }
    public PendingBreakpointDto? Breakpoint { get; set; }
}

public sealed class GetBreakpointRequest
{
    public string BreakpointId { get; set; } = string.Empty;
    public bool IncludeHeaders { get; set; }
}

public sealed class BreakpointDetailsResponse
{
    public PendingBreakpointDto Breakpoint { get; set; } = new PendingBreakpointDto();
    public List<HeaderDto>? RequestHeaders { get; set; }
    public List<HeaderDto>? ResponseHeaders { get; set; }
    public long RequestBodyBytes { get; set; }
    public long ResponseBodyBytes { get; set; }
}

public sealed class UpdateBreakpointRequest
{
    public string BreakpointId { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string? Url { get; set; }
    public int? StatusCode { get; set; }
    public string? StatusDescription { get; set; }
    public List<HeaderDto> SetHeaders { get; set; } = new List<HeaderDto>();
    public string[] RemoveHeaders { get; set; } = Array.Empty<string>();
    public string? BodyBase64 { get; set; }
}

public sealed class BreakpointActionRequest
{
    public string BreakpointId { get; set; } = string.Empty;
    public bool Confirm { get; set; }
}

public sealed class BreakpointActionResponse
{
    public string BreakpointId { get; set; } = string.Empty;
    public int SessionId { get; set; }
    public string Action { get; set; } = string.Empty;
}
