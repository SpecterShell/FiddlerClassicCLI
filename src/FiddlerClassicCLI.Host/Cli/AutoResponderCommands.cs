// Builds AutoResponder configuration, rule management, ordering, and FARX CLI commands.
using System.CommandLine;
using System.CommandLine.Parsing;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Cli;

internal static class AutoResponderCommands
{
    /// <summary>
    /// Creates the complete AutoResponder command tree.
    /// </summary>
    /// <param name="actions">The shared CLI operation facade.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command Create(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("autoresponder", "Configure Fiddler AutoResponder and manage ordered rules.");
        command.Subcommands.Add(CreateStatus(actions, jsonOption));
        command.Subcommands.Add(CreateConfigure(actions, jsonOption));
        command.Subcommands.Add(CreateRules(actions, jsonOption));
        return command;
    }

    private static Command CreateStatus(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("status", "Show AutoResponder switches and rule state.");
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.GetAutoResponder(cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteStatus));
        return command;
    }

    /// <summary>
    /// Creates paired switches that update only explicitly selected AutoResponder settings.
    /// </summary>
    /// <param name="actions">The shared CLI operation facade.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateConfigure(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("configure", "Update AutoResponder behavior switches.");
        var enable = Flag("--enable", "Enable AutoResponder rule evaluation.");
        var disable = Flag("--disable", "Disable AutoResponder rule evaluation.");
        var permitFallthrough = Flag("--permit-fallthrough", "Send unmatched requests to the network.");
        var blockUnmatched = Flag("--block-unmatched", "Block unmatched requests.");
        var acceptConnects = Flag("--accept-connects", "Allow unmatched CONNECT tunnels.");
        var rejectConnects = Flag("--reject-connects", "Reject unmatched CONNECT tunnels.");
        var useLatency = Flag("--use-latency", "Apply per-rule latency values.");
        var ignoreLatency = Flag("--ignore-latency", "Ignore per-rule latency values.");
        AddOptions(command, enable, disable, permitFallthrough, blockUnmatched, acceptConnects, rejectConnects, useLatency, ignoreLatency);
        command.Validators.Add(result =>
        {
            ValidatePair(result, enable, disable);
            ValidatePair(result, permitFallthrough, blockUnmatched);
            ValidatePair(result, acceptConnects, rejectConnects);
            ValidatePair(result, useLatency, ignoreLatency);
            if (!new[] { enable, disable, permitFallthrough, blockUnmatched, acceptConnects, rejectConnects, useLatency, ignoreLatency }
                .Any(option => result.GetValue(option)))
            {
                result.AddError("Select at least one AutoResponder setting.");
            }
        });
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.ConfigureAutoResponder(
                new ConfigureAutoResponderRequest
                {
                    IsEnabled = SelectedPair(parseResult, enable, disable),
                    PermitFallthrough = SelectedPair(parseResult, permitFallthrough, blockUnmatched),
                    AcceptAllConnects = SelectedPair(parseResult, acceptConnects, rejectConnects),
                    UseLatency = SelectedPair(parseResult, useLatency, ignoreLatency)
                },
                cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteStatus));
        return command;
    }

    private static Command CreateRules(CliActions actions, Option<bool> jsonOption)
    {
        var rules = new Command("rules", "List, add, update, order, remove, import, and export rules.");
        rules.Subcommands.Add(CreateList(actions, jsonOption));
        rules.Subcommands.Add(CreateAdd(actions, jsonOption));
        rules.Subcommands.Add(CreateUpdate(actions, jsonOption));
        rules.Subcommands.Add(CreateMove(actions, jsonOption));
        rules.Subcommands.Add(CreateRemove(actions, jsonOption));
        rules.Subcommands.Add(CreateClear(actions, jsonOption));
        rules.Subcommands.Add(CreateSave(actions, jsonOption));
        rules.Subcommands.Add(CreateLoad(actions, jsonOption));
        return rules;
    }

    private static Command CreateList(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("list", "List AutoResponder rules in evaluation order.");
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.ListAutoResponderRules(cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteRules));
        return command;
    }

    private static Command CreateAdd(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("add", "Append one AutoResponder match/action rule.");
        var match = new Argument<string>("match") { Description = "Fiddler AutoResponder match expression." };
        var action = new Argument<string>("action") { Description = "Fiddler AutoResponder action string." };
        var disabled = Flag("--disabled", "Create the rule disabled.");
        var comment = new Option<string?>("--comment") { Description = "Optional single-line comment." };
        var disableOnMatch = Flag("--disable-on-match", "Disable the rule after its first match.");
        var latency = new Option<int>("--latency-ms") { Description = "Rule latency in milliseconds.", DefaultValueFactory = _ => 0 };
        CommandHelpers.AddRangeValidator(latency, 0, ProtocolConstants.MaxBreakpointHoldMilliseconds, "Latency");
        command.Arguments.Add(match);
        command.Arguments.Add(action);
        AddOptions(command, disabled, comment, disableOnMatch, latency);
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.AddAutoResponderRule(
                new AddAutoResponderRuleRequest
                {
                    Match = parseResult.GetRequiredValue(match),
                    Action = parseResult.GetRequiredValue(action),
                    IsEnabled = !parseResult.GetValue(disabled),
                    Comment = parseResult.GetValue(comment),
                    DisableOnMatch = parseResult.GetValue(disableOnMatch),
                    LatencyMilliseconds = parseResult.GetValue(latency)
                },
                cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteMutation));
        return command;
    }

    /// <summary>
    /// Creates partial rule updates while preserving the distinction between an omitted and cleared comment.
    /// </summary>
    /// <param name="actions">The shared CLI operation facade.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateUpdate(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("update", "Update selected fields on one AutoResponder rule.");
        var ruleId = new Argument<string>("rule-id") { Description = "Rule ID returned by rules list." };
        var match = new Option<string?>("--match") { Description = "Replacement match expression." };
        var action = new Option<string?>("--action") { Description = "Replacement action string." };
        var enable = Flag("--enable", "Enable the rule.");
        var disable = Flag("--disable", "Disable the rule.");
        var comment = new Option<string?>("--comment") { Description = "Replacement single-line comment." };
        var clearComment = Flag("--clear-comment", "Clear the current comment.");
        var disableOnMatch = Flag("--disable-on-match", "Disable the rule after its first match.");
        var keepEnabled = Flag("--keep-enabled", "Keep the rule enabled after a match.");
        var latency = new Option<int?>("--latency-ms") { Description = "Replacement latency in milliseconds." };
        CommandHelpers.AddRangeValidator(latency, 0, ProtocolConstants.MaxBreakpointHoldMilliseconds, "Latency");
        command.Arguments.Add(ruleId);
        AddOptions(command, match, action, enable, disable, comment, clearComment, disableOnMatch, keepEnabled, latency);
        command.Validators.Add(result =>
        {
            ValidatePair(result, enable, disable);
            ValidatePair(result, disableOnMatch, keepEnabled);
            if (result.GetResult(comment) is { Implicit: false } && result.GetValue(clearComment))
            {
                result.AddError("--comment and --clear-comment cannot be combined.");
            }

            if (result.GetResult(match) is not { Implicit: false }
                && result.GetResult(action) is not { Implicit: false }
                && !result.GetValue(enable)
                && !result.GetValue(disable)
                && result.GetResult(comment) is not { Implicit: false }
                && !result.GetValue(clearComment)
                && !result.GetValue(disableOnMatch)
                && !result.GetValue(keepEnabled)
                && result.GetResult(latency) is not { Implicit: false })
            {
                result.AddError("Select at least one rule field to update.");
            }
        });
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.UpdateAutoResponderRule(
                new UpdateAutoResponderRuleRequest
                {
                    RuleId = parseResult.GetRequiredValue(ruleId),
                    Match = parseResult.GetValue(match),
                    Action = parseResult.GetValue(action),
                    IsEnabled = SelectedPair(parseResult, enable, disable),
                    Comment = parseResult.GetValue(clearComment) ? null : parseResult.GetValue(comment),
                    SetComment = parseResult.GetValue(clearComment) || parseResult.GetValue(comment) != null,
                    DisableOnMatch = SelectedPair(parseResult, disableOnMatch, keepEnabled),
                    LatencyMilliseconds = parseResult.GetValue(latency)
                },
                cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteMutation));
        return command;
    }

    private static Command CreateMove(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("move", "Move a rule to a zero-based evaluation index.");
        var ruleId = new Argument<string>("rule-id");
        var index = new Argument<int>("index");
        command.Arguments.Add(ruleId);
        command.Arguments.Add(index);
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.MoveAutoResponderRule(
                new MoveAutoResponderRuleRequest
                {
                    RuleId = parseResult.GetRequiredValue(ruleId),
                    Index = parseResult.GetRequiredValue(index)
                },
                cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteMutation));
        return command;
    }

    private static Command CreateRemove(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("remove", "Remove one AutoResponder rule.");
        var ruleId = new Argument<string>("rule-id");
        var yes = Flag("--yes", "Confirm removal without prompting.", "-y");
        command.Arguments.Add(ruleId);
        command.Options.Add(yes);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            if (!CliOutput.Confirm($"Remove AutoResponder rule '{parseResult.GetRequiredValue(ruleId)}'?", parseResult.GetValue(yes)))
            {
                return Task.FromResult(ConfirmationRequired(json));
            }

            return CliOutput.RunAsync(
                () => actions.RemoveAutoResponderRule(parseResult.GetRequiredValue(ruleId), cancellationToken),
                json,
                WriteMutation);
        });
        return command;
    }

    private static Command CreateClear(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("clear", "Remove every AutoResponder rule.");
        var yes = Flag("--yes", "Confirm clearing without prompting.", "-y");
        command.Options.Add(yes);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            if (!CliOutput.Confirm("Clear all AutoResponder rules?", parseResult.GetValue(yes)))
            {
                return Task.FromResult(ConfirmationRequired(json));
            }

            return CliOutput.RunAsync(() => actions.ClearAutoResponderRules(cancellationToken), json, WriteMutation);
        });
        return command;
    }

    private static Command CreateSave(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("save", "Save all AutoResponder rules to an absolute FARX path.");
        var path = new Argument<string>("path");
        var overwrite = Flag("--overwrite", "Allow replacement of an existing FARX file.");
        var yes = Flag("--yes", "Confirm replacement without prompting.", "-y");
        command.Arguments.Add(path);
        AddOptions(command, overwrite, yes);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var destination = parseResult.GetRequiredValue(path);
            var allowOverwrite = parseResult.GetValue(overwrite);
            var confirmed = !File.Exists(destination)
                || (allowOverwrite && CliOutput.Confirm($"Replace AutoResponder file '{destination}'?", parseResult.GetValue(yes)));
            if (File.Exists(destination) && !confirmed && allowOverwrite)
            {
                return Task.FromResult(ConfirmationRequired(json));
            }

            return CliOutput.RunAsync(
                () => actions.SaveAutoResponderRules(destination, allowOverwrite, confirmed, cancellationToken),
                json,
                result => Console.WriteLine($"Saved {result.RuleCount} rule(s) to {result.Path}"));
        });
        return command;
    }

    private static Command CreateLoad(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("load", "Import a FARX file or replace the current rule list.");
        var path = new Argument<string>("path");
        var replace = Flag("--replace", "Replace the complete current rule list.");
        var yes = Flag("--yes", "Confirm replacement without prompting.", "-y");
        command.Arguments.Add(path);
        AddOptions(command, replace, yes);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var replacing = parseResult.GetValue(replace);
            if (replacing && !CliOutput.Confirm("Replace all AutoResponder rules from this FARX file?", parseResult.GetValue(yes)))
            {
                return Task.FromResult(ConfirmationRequired(json));
            }

            return CliOutput.RunAsync(
                () => actions.LoadAutoResponderRules(parseResult.GetRequiredValue(path), replacing, cancellationToken),
                json,
                result => Console.WriteLine($"Loaded {result.RuleCount} total rule(s) from {result.Path}"));
        });
        return command;
    }

    private static void WriteStatus(AutoResponderStatusResponse result)
    {
        Console.WriteLine($"Enabled:             {result.IsEnabled}");
        Console.WriteLine($"Permit fallthrough:  {result.PermitFallthrough}");
        Console.WriteLine($"Accept CONNECTs:     {result.AcceptAllConnects}");
        Console.WriteLine($"Use latency:         {result.UseLatency}");
        Console.WriteLine($"Rules:               {result.RuleCount}");
        Console.WriteLine($"Unsaved changes:     {result.IsRuleListDirty}");
    }

    private static void WriteRules(ListAutoResponderRulesResponse result)
    {
        Console.WriteLine($"Rules: {result.Rules.Count}");
        foreach (var rule in result.Rules)
        {
            Console.WriteLine($"{rule.Index,4} {(rule.IsEnabled ? "on " : "off")} {rule.RuleId} {rule.Match} -> {rule.Action}");
        }
    }

    private static void WriteMutation(AutoResponderMutationResponse result)
    {
        Console.WriteLine($"Affected rules: {result.AffectedCount}");
        if (result.Rule != null)
        {
            Console.WriteLine($"{result.Rule.Index}: {result.Rule.RuleId} {result.Rule.Match} -> {result.Rule.Action}");
        }
    }

    private static Option<bool> Flag(string name, string description, params string[] aliases)
    {
        return new Option<bool>(name, aliases) { Description = description };
    }

    private static void AddOptions(Command command, params Option[] options)
    {
        foreach (var option in options)
        {
            command.Options.Add(option);
        }
    }

    private static void ValidatePair(CommandResult result, Option<bool> positive, Option<bool> negative)
    {
        if (result.GetValue(positive) && result.GetValue(negative))
        {
            result.AddError($"{positive.Name} and {negative.Name} cannot be combined.");
        }
    }

    private static bool? SelectedPair(ParseResult result, Option<bool> positive, Option<bool> negative)
    {
        return result.GetValue(positive) ? true : result.GetValue(negative) ? false : null;
    }

    private static int ConfirmationRequired(bool json)
    {
        return CliOutput.Error(
            new BridgeClientException(ErrorCodes.ConfirmationRequired, "Confirmation is required. Use --yes in non-interactive use."),
            json);
    }
}
