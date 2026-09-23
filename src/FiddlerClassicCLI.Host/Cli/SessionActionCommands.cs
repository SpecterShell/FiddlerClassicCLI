// Builds confirmed session mutations, archive operations, replay, and evidence exports.
using System.CommandLine;
using FiddlerClassicCLI.Protocol;
using static FiddlerClassicCLI.Host.Cli.CommandHelpers;

namespace FiddlerClassicCLI.Host.Cli;

internal static class SessionActionCommands
{
    /// <summary>
    /// Creates the destructive clear command and enforces interactive or explicit confirmation.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command CreateClear(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("clear", "Permanently clear all sessions from Fiddler.");
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm without prompting." };
        command.Options.Add(yes);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            if (!CliOutput.Confirm("Clear all captured Fiddler sessions?", parseResult.GetValue(yes)))
            {
                return Task.FromResult(ConfirmationRequired(json));
            }

            return CliOutput.RunAsync(
                () => actions.ClearSessions(cancellationToken),
                json,
                result => Console.WriteLine($"Cleared {result.RemovedCount} sessions."));
        });
        return command;
    }

    /// <summary>
    /// Creates selective destructive removal with interactive or explicit confirmation.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command CreateRemove(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("remove", "Permanently remove only the specified sessions.");
        var ids = new Option<int[]>("--ids")
        {
            Description = "Session IDs to remove.",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm without prompting." };
        AddOptions(command, ids, yes);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var sessionIds = parseResult.GetValue(ids) ?? Array.Empty<int>();
            if (!CliOutput.Confirm($"Remove {sessionIds.Length} captured Fiddler sessions?", parseResult.GetValue(yes)))
            {
                return Task.FromResult(ConfirmationRequired(json));
            }

            return CliOutput.RunAsync(
                () => actions.RemoveSessions(sessionIds, cancellationToken),
                json,
                result => Console.WriteLine($"Removed {result.RemovedCount} sessions."));
        });
        return command;
    }

    /// <summary>
    /// Creates the SAZ export command with session selection and overwrite confirmation.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command CreateSave(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("save", "Save sessions to an absolute .saz archive path.");
        var path = new Argument<string>("path") { Description = "Absolute destination .saz path." };
        var ids = new Option<int[]>("--ids")
        {
            Description = "Session IDs to save. Omit to save all.",
            AllowMultipleArgumentsPerToken = true
        };
        var overwrite = new Option<bool>("--overwrite") { Description = "Allow replacing an existing archive." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm archive replacement without prompting." };
        command.Arguments.Add(path);
        AddOptions(command, ids, overwrite, yes);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var archivePath = parseResult.GetRequiredValue(path);
            var allowOverwrite = parseResult.GetValue(overwrite);
            var confirmOverwrite = !File.Exists(archivePath);
            if (File.Exists(archivePath) && allowOverwrite)
            {
                confirmOverwrite = CliOutput.Confirm($"Replace existing archive '{archivePath}'?", parseResult.GetValue(yes));
                if (!confirmOverwrite)
                {
                    return Task.FromResult(ConfirmationRequired(json));
                }
            }

            var sessionIds = parseResult.GetValue(ids);
            return CliOutput.RunAsync(
                () => actions.SaveSessions(
                    archivePath,
                    sessionIds is { Length: > 0 } ? sessionIds : null,
                    allowOverwrite,
                    confirmOverwrite,
                    cancellationToken),
                json,
                result => Console.WriteLine($"Saved {result.SessionCount} sessions to {result.Path}"));
        });
        return command;
    }

    /// <summary>
    /// Creates the SAZ import command for an absolute archive path.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command CreateLoad(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("load", "Load sessions from an absolute .saz archive path.");
        var path = new Argument<string>("path") { Description = "Absolute source .saz path." };
        command.Arguments.Add(path);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => actions.LoadSessions(parseResult.GetRequiredValue(path), cancellationToken),
                json,
                result => Console.WriteLine($"Loaded {result.SessionCount} sessions from {result.Path}"));
        });
        return command;
    }

    /// <summary>
    /// Creates the replay command and binds conditional or unconditional replay behavior.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command CreateReplay(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("replay", "Queue replay of a captured session through Fiddler.");
        var sessionId = new Argument<int>("session-id") { Description = "Fiddler session ID." };
        var unconditional = new Option<bool>("--unconditional") { Description = "Remove conditional request headers before replay." };
        var wait = new Option<bool>("--wait") { Description = "Wait for the resulting completed session." };
        var timeout = CreateTimeoutOption();
        command.Arguments.Add(sessionId);
        AddOptions(command, unconditional, wait, timeout);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => actions.ReplaySession(
                    parseResult.GetValue(sessionId),
                    parseResult.GetValue(unconditional),
                    parseResult.GetValue(wait),
                    parseResult.GetValue(timeout) * 1000,
                    cancellationToken),
                json,
                WriteQueuedResult);
        });
        return command;
    }

    /// <summary>
    /// Creates cURL, raw HTTP, and filtered HAR export with explicit overwrite handling.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command CreateExport(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("export", "Export sensitive captured evidence as cURL, raw HTTP, or HAR.");
        var sessionId = new Argument<int?>("session-id")
        {
            Description = "Session ID for curl or raw-http. Omit for HAR.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var format = new Option<string>("--format", "-f")
        {
            Description = "Export format: curl, raw-http, or har.",
            DefaultValueFactory = _ => "curl"
        };
        format.AcceptOnlyFromAmong("curl", "raw-http", "har");
        var output = new Option<string>("--output", "-o")
        {
            Description = "Destination path, or '-' for single-session output.",
            Required = true
        };
        var overwrite = new Option<bool>("--overwrite") { Description = "Allow replacing an existing output file." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm replacement without prompting." };
        var filters = new SessionFilterOptions();
        var limit = new Option<int>("--limit", "-n")
        {
            Description = "Maximum HAR entries (1-1000).",
            DefaultValueFactory = _ => ProtocolConstants.DefaultSessionLimit
        };
        AddRangeValidator(limit, 1, ProtocolConstants.MaxSessionLimit, "Limit");
        command.Arguments.Add(sessionId);
        filters.AddTo(command);
        AddOptions(command, format, output, overwrite, yes, limit);
        command.Validators.Add(result =>
        {
            string? selectedFormat;
            bool hasSessionId;
            try
            {
                selectedFormat = result.GetValue(format);
                hasSessionId = result.GetValue(sessionId).HasValue;
            }
            catch (InvalidOperationException)
            {
                // The parser reports conversion failures after command validators have run.
                return;
            }
            if (string.Equals(selectedFormat, "har", StringComparison.OrdinalIgnoreCase) == hasSessionId)
            {
                result.AddError("HAR export omits session-id. curl and raw-http require session-id.");
            }
        });
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var selectedFormat = parseResult.GetRequiredValue(format);
            var outputPath = parseResult.GetRequiredValue(output);
            var id = parseResult.GetValue(sessionId);
            var allowOverwrite = parseResult.GetValue(overwrite);
            if (outputPath != "-" && File.Exists(outputPath))
            {
                if (!allowOverwrite || !CliOutput.Confirm($"Replace existing export '{outputPath}'?", parseResult.GetValue(yes)))
                {
                    return ConfirmationRequired(json);
                }
            }

            try
            {
                var result = await (id.HasValue
                    ? actions.ExportSession(id.Value, selectedFormat, outputPath, allowOverwrite, cancellationToken)
                    : actions.ExportHar(
                        CreateExportFilters(filters.CreateRequest(parseResult), parseResult.GetValue(limit)),
                        outputPath,
                        allowOverwrite,
                        cancellationToken)).ConfigureAwait(false);
                if (outputPath == "-")
                {
                    Console.Error.WriteLine($"Exported {result.SessionCount} session as {result.Format}.");
                }
                else
                {
                    CliOutput.Write(
                        result,
                        json,
                        () => Console.WriteLine($"Exported {result.SessionCount} session(s) as {result.Format} to {result.Path}"));
                }

                return ExitCodes.Success;
            }
            catch (Exception exception)
            {
                return CliOutput.Error(exception, json);
            }
        });
        return command;
    }

    private static ListSessionsRequest CreateExportFilters(ListSessionsRequest filters, int limit)
    {
        filters.Limit = limit;
        filters.NewestFirst = false;
        return filters;
    }
}
