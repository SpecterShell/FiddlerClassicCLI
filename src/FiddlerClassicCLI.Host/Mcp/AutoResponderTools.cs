// Exposes safety-annotated Fiddler AutoResponder configuration and rule operations as MCP tools.
using System.ComponentModel;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Protocol;
using ModelContextProtocol.Server;

namespace FiddlerClassicCLI.Host.Mcp;

[McpServerToolType]
internal sealed class AutoResponderTools
{
    private readonly IBridgeClient _bridgeClient;

    /// <summary>
    /// Creates the AutoResponder MCP tool set over the shared bridge client.
    /// </summary>
    /// <param name="bridgeClient">Sends typed operations to the loaded Fiddler extension.</param>
    public AutoResponderTools(IBridgeClient bridgeClient)
    {
        _bridgeClient = bridgeClient;
    }

    [McpServerTool(Name = "get_autoresponder_status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Get Fiddler AutoResponder switches, rule count, and unsaved-change state.")]
    public Task<AutoResponderStatusResponse> GetStatus(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<EmptyRequest, AutoResponderStatusResponse>(
            Operations.GetAutoResponder,
            new EmptyRequest(),
            cancellationToken);
    }

    /// <summary>
    /// Updates only supplied AutoResponder behavior switches.
    /// </summary>
    /// <param name="isEnabled">Whether AutoResponder evaluates rules.</param>
    /// <param name="permitFallthrough">Whether unmatched requests continue to the network.</param>
    /// <param name="acceptAllConnects">Whether unmatched CONNECT tunnels are accepted.</param>
    /// <param name="useLatency">Whether per-rule latency is applied.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "configure_autoresponder", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Update supplied AutoResponder switches. This can change how future network traffic is handled.")]
    public Task<AutoResponderStatusResponse> Configure(
        [Description("Enable or disable AutoResponder. Omit to preserve the current value.")] bool? isEnabled = null,
        [Description("Permit unmatched requests to reach the network. Omit to preserve the current value.")] bool? permitFallthrough = null,
        [Description("Accept unmatched CONNECT tunnels. Omit to preserve the current value.")] bool? acceptAllConnects = null,
        [Description("Apply per-rule latency. Omit to preserve the current value.")] bool? useLatency = null,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<ConfigureAutoResponderRequest, AutoResponderStatusResponse>(
            Operations.ConfigureAutoResponder,
            new ConfigureAutoResponderRequest
            {
                IsEnabled = isEnabled,
                PermitFallthrough = permitFallthrough,
                AcceptAllConnects = acceptAllConnects,
                UseLatency = useLatency
            },
            cancellationToken);
    }

    [McpServerTool(Name = "list_autoresponder_rules", ReadOnly = true, UseStructuredContent = true)]
    [Description("List every AutoResponder rule in evaluation order with a runtime-stable rule ID.")]
    public Task<ListAutoResponderRulesResponse> ListRules(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<EmptyRequest, ListAutoResponderRulesResponse>(
            Operations.ListAutoResponderRules,
            new EmptyRequest(),
            cancellationToken);
    }

    /// <summary>
    /// Appends one raw Fiddler AutoResponder match/action rule.
    /// </summary>
    /// <param name="match">The exact Fiddler match expression.</param>
    /// <param name="action">The exact Fiddler action string.</param>
    /// <param name="isEnabled">Whether the new rule is active.</param>
    /// <param name="comment">An optional single-line comment.</param>
    /// <param name="disableOnMatch">Whether Fiddler disables the rule after a match.</param>
    /// <param name="latencyMs">The optional rule latency.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "add_autoresponder_rule", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Append a raw Fiddler AutoResponder rule. Actions may read local files, redirect, synthesize, delay, drop, or otherwise change network traffic.")]
    public Task<AutoResponderMutationResponse> AddRule(
        [Description("Fiddler AutoResponder match expression.")] string match,
        [Description("Fiddler AutoResponder action string.")] string action,
        [Description("Create the rule enabled.")] bool isEnabled = true,
        [Description("Optional single-line rule comment.")] string? comment = null,
        [Description("Disable the rule after its first match.")] bool disableOnMatch = false,
        [Description("Rule latency in milliseconds, from 0 through 300000.")] int latencyMs = 0,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<AddAutoResponderRuleRequest, AutoResponderMutationResponse>(
            Operations.AddAutoResponderRule,
            new AddAutoResponderRuleRequest
            {
                Match = match,
                Action = action,
                IsEnabled = isEnabled,
                Comment = comment,
                DisableOnMatch = disableOnMatch,
                LatencyMilliseconds = latencyMs
            },
            cancellationToken);
    }

    /// <summary>
    /// Updates selected fields on an existing AutoResponder rule.
    /// </summary>
    /// <param name="ruleId">The runtime ID returned by list_autoresponder_rules.</param>
    /// <param name="match">The optional replacement match expression.</param>
    /// <param name="action">The optional replacement action string.</param>
    /// <param name="isEnabled">The optional enabled state.</param>
    /// <param name="comment">The optional replacement comment.</param>
    /// <param name="setComment">Whether comment should be applied, including null to clear it.</param>
    /// <param name="disableOnMatch">The optional disable-after-match state.</param>
    /// <param name="latencyMs">The optional replacement latency.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    [McpServerTool(Name = "update_autoresponder_rule", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Update selected fields on one AutoResponder rule. Changed actions affect future matching network traffic.")]
    public Task<AutoResponderMutationResponse> UpdateRule(
        [Description("Rule ID returned by list_autoresponder_rules.")] string ruleId,
        [Description("Replacement match expression. Omit to preserve.")] string? match = null,
        [Description("Replacement action string. Omit to preserve.")] string? action = null,
        [Description("Replacement enabled state. Omit to preserve.")] bool? isEnabled = null,
        [Description("Replacement comment. Use setComment=true with null to clear.")] string? comment = null,
        [Description("Apply the comment argument, including null to clear it.")] bool setComment = false,
        [Description("Replacement disable-after-match state. Omit to preserve.")] bool? disableOnMatch = null,
        [Description("Replacement latency in milliseconds. Omit to preserve.")] int? latencyMs = null,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<UpdateAutoResponderRuleRequest, AutoResponderMutationResponse>(
            Operations.UpdateAutoResponderRule,
            new UpdateAutoResponderRuleRequest
            {
                RuleId = ruleId,
                Match = match,
                Action = action,
                IsEnabled = isEnabled,
                Comment = comment,
                SetComment = setComment || comment != null,
                DisableOnMatch = disableOnMatch,
                LatencyMilliseconds = latencyMs
            },
            cancellationToken);
    }

    [McpServerTool(Name = "move_autoresponder_rule", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Move one AutoResponder rule to a zero-based evaluation index. Rule order changes which action handles matching traffic.")]
    public Task<AutoResponderMutationResponse> MoveRule(
        [Description("Rule ID returned by list_autoresponder_rules.")] string ruleId,
        [Description("Zero-based destination index.")] int index,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<MoveAutoResponderRuleRequest, AutoResponderMutationResponse>(
            Operations.MoveAutoResponderRule,
            new MoveAutoResponderRuleRequest { RuleId = ruleId, Index = index },
            cancellationToken);
    }

    [McpServerTool(Name = "remove_autoresponder_rule", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Permanently remove one AutoResponder rule. Requires confirm=true.")]
    public Task<AutoResponderMutationResponse> RemoveRule(
        [Description("Rule ID returned by list_autoresponder_rules.")] string ruleId,
        [Description("Must be true to confirm rule removal.")] bool confirm,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<RemoveAutoResponderRuleRequest, AutoResponderMutationResponse>(
            Operations.RemoveAutoResponderRule,
            new RemoveAutoResponderRuleRequest { RuleId = ruleId, Confirm = confirm },
            cancellationToken);
    }

    [McpServerTool(Name = "clear_autoresponder_rules", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Permanently remove every AutoResponder rule. Requires confirm=true.")]
    public Task<AutoResponderMutationResponse> ClearRules(
        [Description("Must be true to confirm clearing every rule.")] bool confirm,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<ClearAutoResponderRulesRequest, AutoResponderMutationResponse>(
            Operations.ClearAutoResponderRules,
            new ClearAutoResponderRulesRequest { Confirm = confirm },
            cancellationToken);
    }

    [McpServerTool(Name = "save_autoresponder_rules", ReadOnly = false, Destructive = true, Idempotent = false, UseStructuredContent = true)]
    [Description("Save every rule to an absolute .farx path. Existing files require overwrite=true and confirmOverwrite=true.")]
    public Task<AutoResponderRulesFileResponse> SaveRules(
        [Description("Absolute destination path ending in .farx.")] string path,
        [Description("Allow replacement of an existing file.")] bool overwrite = false,
        [Description("Explicitly confirm replacement of an existing file.")] bool confirmOverwrite = false,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<SaveAutoResponderRulesRequest, AutoResponderRulesFileResponse>(
            Operations.SaveAutoResponderRules,
            new SaveAutoResponderRulesRequest
            {
                Path = path,
                Overwrite = overwrite,
                ConfirmOverwrite = confirmOverwrite
            },
            cancellationToken);
    }

    [McpServerTool(Name = "load_autoresponder_rules", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Import an absolute .farx file, or replace every current rule when replace=true and confirmReplace=true.")]
    public Task<AutoResponderRulesFileResponse> LoadRules(
        [Description("Absolute source path ending in .farx.")] string path,
        [Description("Replace all current rules when true. False imports into the current rules.")] bool replace = false,
        [Description("Must be true when replace=true.")] bool confirmReplace = false,
        CancellationToken cancellationToken = default)
    {
        return _bridgeClient.SendAsync<LoadAutoResponderRulesRequest, AutoResponderRulesFileResponse>(
            Operations.LoadAutoResponderRules,
            new LoadAutoResponderRulesRequest
            {
                Path = path,
                Replace = replace,
                ConfirmReplace = confirmReplace
            },
            cancellationToken);
    }
}
