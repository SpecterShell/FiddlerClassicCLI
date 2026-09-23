// Exposes safety-annotated managed request and response breakpoint operations as MCP tools.
using System.ComponentModel;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Protocol;
using ModelContextProtocol.Server;

namespace FiddlerClassicCLI.Host.Mcp;

[McpServerToolType]
internal sealed class BreakpointTools
{
    private readonly IBridgeClient _bridgeClient;

    /// <summary>
    /// Creates the breakpoint MCP tool set over the shared bridge client.
    /// </summary>
    /// <param name="bridgeClient">Sends typed operations to the loaded Fiddler extension.</param>
    public BreakpointTools(IBridgeClient bridgeClient)
    {
        _bridgeClient = bridgeClient;
    }

    [McpServerTool(Name = "list_network_breakpoint_arms", ReadOnly = true, UseStructuredContent = true)]
    [Description("List active request and response breakpoint arms in creation order.")]
    public Task<ListBreakpointArmsResponse> ListArms(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<EmptyRequest, ListBreakpointArmsResponse>(
            Operations.ListBreakpointArms,
            new EmptyRequest(),
            cancellationToken);
    }

    /// <summary>
    /// Arms a bounded future request or response breakpoint.
    /// </summary>
    /// <param name="stage">The request or response pause stage.</param>
    /// <param name="method">An optional exact method.</param>
    /// <param name="host">An optional host substring.</param>
    /// <param name="urlContains">An optional full URL substring.</param>
    /// <param name="statusCode">An optional response status.</param>
    /// <param name="contentType">An optional response content-type substring.</param>
    /// <param name="process">An optional client process substring.</param>
    /// <param name="headerName">An optional exact stage header name.</param>
    /// <param name="headerValue">An optional stage header value substring.</param>
    /// <param name="oneShot">Whether the arm is removed after its first match.</param>
    /// <param name="holdMs">How long a managed pause remains before automatic resume.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "arm_network_breakpoint", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Arm a future request or response pause. Managed pauses automatically resume after holdMs. One-shot arms are recommended.")]
    public Task<BreakpointArmMutationResponse> Arm(
        [Description("Breakpoint stage: request or response.")] string stage,
        [Description("Exact HTTP method filter.")] string? method = null,
        [Description("Case-insensitive host substring filter.")] string? host = null,
        [Description("Case-insensitive full URL substring filter.")] string? urlContains = null,
        [Description("Exact response status filter for the response stage only.")] int? statusCode = null,
        [Description("Response content-type substring for the response stage only.")] string? contentType = null,
        [Description("Client process substring filter.")] string? process = null,
        [Description("Exact request or response header name for the selected stage.")] string? headerName = null,
        [Description("Header value substring. Requires headerName.")] string? headerValue = null,
        [Description("Remove the arm after its first match.")] bool oneShot = true,
        [Description("Automatic hold timeout in milliseconds, from 1 through 300000.")] int holdMs = ProtocolConstants.DefaultBreakpointHoldMilliseconds,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<ArmBreakpointRequest, BreakpointArmMutationResponse>(
            Operations.ArmBreakpoint,
            new ArmBreakpointRequest
            {
                Stage = stage,
                Filter = new BreakpointFilterDto
                {
                    Method = method,
                    Host = host,
                    UrlContains = urlContains,
                    StatusCode = statusCode,
                    ContentType = contentType,
                    Process = process,
                    HeaderName = headerName,
                    HeaderValue = headerValue
                },
                OneShot = oneShot,
                HoldMilliseconds = holdMs
            },
            cancellationToken);
    }

    [McpServerTool(Name = "disarm_network_breakpoint", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Remove one breakpoint arm. Sessions already paused by it remain paused until resumed, aborted, or timed out.")]
    public Task<BreakpointArmMutationResponse> Disarm(
        [Description("Arm ID returned by arm_network_breakpoint or list_network_breakpoint_arms.")] string armId,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<DisarmBreakpointRequest, BreakpointArmMutationResponse>(
            Operations.DisarmBreakpoint,
            new DisarmBreakpointRequest { ArmId = armId },
            cancellationToken);
    }

    [McpServerTool(Name = "list_pending_network_breakpoints", ReadOnly = true, UseStructuredContent = true)]
    [Description("List sessions currently paused at request or response breakpoints. Headers and bodies are not included.")]
    public Task<ListPendingBreakpointsResponse> ListPending(
        [Description("Optional breakpoint stage: request or response.")] string? stage = null,
        [Description("Optional exact Fiddler request/session ID.")] int? requestId = null,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<ListPendingBreakpointsRequest, ListPendingBreakpointsResponse>(
            Operations.ListPendingBreakpoints,
            new ListPendingBreakpointsRequest { Stage = stage, SessionId = requestId },
            cancellationToken);
    }

    [McpServerTool(Name = "wait_for_network_breakpoint", ReadOnly = true, UseStructuredContent = true)]
    [Description("Wait for the first currently paused breakpoint after an exclusive sequence cursor. Returns matched=false on timeout.")]
    public Task<WaitForBreakpointResponse> Wait(
        [Description("Exclusive sequence cursor. Use 0 for the next available managed pause.")] long afterSequence = 0,
        [Description("Wait duration in milliseconds, from 1 through 60000.")] int timeoutMs = ProtocolConstants.DefaultWaitMilliseconds,
        [Description("Optional breakpoint stage: request or response.")] string? stage = null,
        [Description("Optional exact Fiddler request/session ID.")] int? requestId = null,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<WaitForBreakpointRequest, WaitForBreakpointResponse>(
            Operations.WaitForBreakpoint,
            new WaitForBreakpointRequest
            {
                AfterSequence = afterSequence,
                TimeoutMilliseconds = timeoutMs,
                Stage = stage,
                SessionId = requestId
            },
            cancellationToken);
    }

    [McpServerTool(Name = "get_network_breakpoint", ReadOnly = true, UseStructuredContent = true)]
    [Description("Get one currently paused breakpoint. Exact headers are included only when includeHeaders=true. Use get_network_request_body for body bytes.")]
    public Task<BreakpointDetailsResponse> Get(
        [Description("Breakpoint ID returned by list_pending_network_breakpoints or wait_for_network_breakpoint.")] string breakpointId,
        [Description("Include exact request and response headers.")] bool includeHeaders = false,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<GetBreakpointRequest, BreakpointDetailsResponse>(
            Operations.GetBreakpoint,
            new GetBreakpointRequest { BreakpointId = breakpointId, IncludeHeaders = includeHeaders },
            cancellationToken);
    }

    /// <summary>
    /// Applies stage-appropriate in-flight request or response mutations.
    /// </summary>
    /// <param name="breakpointId">The pending breakpoint ID.</param>
    /// <param name="method">An optional request method replacement.</param>
    /// <param name="url">An optional request URL replacement.</param>
    /// <param name="statusCode">An optional response status replacement.</param>
    /// <param name="statusDescription">An optional response reason phrase replacement.</param>
    /// <param name="setHeaders">Stage headers in Name: value form.</param>
    /// <param name="removeHeaders">Stage header names to remove.</param>
    /// <param name="bodyBase64">An optional complete replacement body.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "update_network_breakpoint", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Mutate a paused request or response before resuming it. Method/URL apply only to request pauses. Status applies only to response pauses. Body is complete base64, maximum 4 MiB.")]
    public Task<BreakpointDetailsResponse> Update(
        [Description("Breakpoint ID returned by list_pending_network_breakpoints or wait_for_network_breakpoint.")] string breakpointId,
        [Description("Replacement request method.")] string? method = null,
        [Description("Replacement absolute HTTP or HTTPS request URL.")] string? url = null,
        [Description("Replacement response status code.")] int? statusCode = null,
        [Description("Replacement response status description.")] string? statusDescription = null,
        [Description("Stage headers in 'Name: value' form.")] string[]? setHeaders = null,
        [Description("Stage header names to remove.")] string[]? removeHeaders = null,
        [Description("Complete replacement body encoded as base64. Omit to preserve the body.")] string? bodyBase64 = null,
        CancellationToken cancellationToken = default)
    {
        RequestInput.ValidateBody(bodyBase64);
        return _bridgeClient.SendAsync<UpdateBreakpointRequest, BreakpointDetailsResponse>(
            Operations.UpdateBreakpoint,
            new UpdateBreakpointRequest
            {
                BreakpointId = breakpointId,
                Method = method,
                Url = url,
                StatusCode = statusCode,
                StatusDescription = statusDescription,
                SetHeaders = RequestInput.ParseHeaders(setHeaders ?? Array.Empty<string>()),
                RemoveHeaders = removeHeaders ?? Array.Empty<string>(),
                BodyBase64 = bodyBase64
            },
            cancellationToken);
    }

    [McpServerTool(Name = "resume_network_breakpoint", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Resume one currently paused request or response so Fiddler can continue processing it.")]
    public Task<BreakpointActionResponse> Resume(
        [Description("Breakpoint ID returned by list_pending_network_breakpoints or wait_for_network_breakpoint.")] string breakpointId,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<BreakpointActionRequest, BreakpointActionResponse>(
            Operations.ResumeBreakpoint,
            new BreakpointActionRequest { BreakpointId = breakpointId },
            cancellationToken);
    }

    [McpServerTool(Name = "abort_network_breakpoint", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Abort one currently paused network session. Requires confirm=true.")]
    public Task<BreakpointActionResponse> Abort(
        [Description("Breakpoint ID returned by list_pending_network_breakpoints or wait_for_network_breakpoint.")] string breakpointId,
        [Description("Must be true to confirm aborting the network session.")] bool confirm,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<BreakpointActionRequest, BreakpointActionResponse>(
            Operations.AbortBreakpoint,
            new BreakpointActionRequest { BreakpointId = breakpointId, Confirm = confirm },
            cancellationToken);
    }
}
