// Builds the complete System.CommandLine command tree and binds CLI handlers.
using System.CommandLine;
using System.Text.Json;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Host.Mcp;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Cli;

internal static class CommandFactory
{
    /// <summary>
    /// Builds the root command, shared JSON option, and all supported command groups.
    /// </summary>
    /// <param name="actions">The application operations bound to command handlers.</param>
    /// <param name="daemonClient">The daemon lifecycle client bound to daemon commands.</param>
    public static RootCommand Create(CliActions actions, DaemonClient daemonClient)
    {
        var root = new RootCommand("Unofficial CLI and MCP server for Fiddler Classic 5.x on Windows.");
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write machine-readable JSON.",
            Recursive = true
        };
        root.Options.Add(jsonOption);

        root.Subcommands.Add(CreateDoctor(actions, jsonOption));
        root.Subcommands.Add(CreateStatus(actions, jsonOption));
        root.Subcommands.Add(CreateCapture(actions, jsonOption));
        root.Subcommands.Add(CreateSessions(actions, jsonOption));
        root.Subcommands.Add(CreateRequest(actions, jsonOption));
        root.Subcommands.Add(AutoResponderCommands.Create(actions, jsonOption));
        root.Subcommands.Add(BreakpointCommands.Create(actions, jsonOption));
        root.Subcommands.Add(CreateBridge(actions, jsonOption));
        root.Subcommands.Add(CreateDaemon(daemonClient, jsonOption));
        root.Subcommands.Add(CreateMcp(actions));
        root.Subcommands.Add(CreateConfig(actions, jsonOption));
        return root;
    }

    /// <summary>
    /// Creates the diagnostic command and maps an unhealthy result to the appropriate exit code.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateDoctor(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("doctor", "Check Fiddler, bridge, process, pipe, and configuration health.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            try
            {
                var result = await actions.Doctor(cancellationToken).ConfigureAwait(false);
                CliOutput.Write(result, json, () => WriteDoctor(result));
                if (result.Healthy)
                {
                    return ExitCodes.Success;
                }

                var installed = result.Checks.First(check => check.Name == "fiddler-installed").Passed;
                return installed ? ExitCodes.Unavailable : ExitCodes.NotInstalled;
            }
            catch (Exception exception)
            {
                return CliOutput.Error(exception, json);
            }
        });
        return command;
    }

    /// <summary>
    /// Creates the status command with structured and human-readable result writers.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateStatus(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("status", "Show Fiddler installation, bridge, proxy, and session status.");
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => actions.Status(cancellationToken),
                json,
                WriteStatus);
        });
        return command;
    }

    /// <summary>
    /// Creates the capture command group for system proxy attachment and detachment.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handlers.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateCapture(CliActions actions, Option<bool> jsonOption)
    {
        var capture = new Command("capture", "Attach or detach Fiddler as the Windows system proxy.");
        capture.Subcommands.Add(CreateCaptureAction("start", true, actions, jsonOption));
        capture.Subcommands.Add(CreateCaptureAction("stop", false, actions, jsonOption));
        return capture;
    }

    /// <summary>
    /// Creates one capture state command and binds the requested proxy state.
    /// </summary>
    /// <param name="name">The command name.</param>
    /// <param name="enabled">The target proxy attachment state.</param>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateCaptureAction(
        string name,
        bool enabled,
        CliActions actions,
        Option<bool> jsonOption)
    {
        var command = new Command(name, enabled ? "Start system proxy capture." : "Stop system proxy capture.");
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => actions.SetCapture(enabled, cancellationToken),
                json,
                result => Console.WriteLine(result.IsProxyAttached ? "Capture is active." : "Capture is stopped."));
        });
        return command;
    }

    /// <summary>
    /// Creates the captured-session command group and its inspection and mutation subcommands.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handlers.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateSessions(CliActions actions, Option<bool> jsonOption)
    {
        var sessions = new Command("sessions", "Inspect and manage captured Fiddler sessions.");
        sessions.Subcommands.Add(CreateSessionsList(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsWatch(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsShow(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsBody(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsClear(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsRemove(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsSave(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsLoad(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsReplay(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsExport(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsDiff(actions, jsonOption));
        sessions.Subcommands.Add(CreateSessionsWebSocket(actions, jsonOption));
        return sessions;
    }

    /// <summary>
    /// Creates the bounded session-list command and maps every filter option into a bridge request.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateSessionsList(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("list", "List captured sessions without headers or bodies.");
        var filters = new SessionFilterOptions();
        var limit = new Option<int>("--limit", "-n")
        {
            Description = "Maximum results (1-1000).",
            DefaultValueFactory = _ => ProtocolConstants.DefaultSessionLimit
        };
        AddRangeValidator(limit, 1, ProtocolConstants.MaxSessionLimit, "Limit");
        var oldestFirst = new Option<bool>("--oldest-first") { Description = "Sort by ascending session ID." };

        filters.AddTo(command);
        AddOptions(command, limit, oldestFirst);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var request = filters.CreateRequest(parseResult);
            request.Limit = parseResult.GetValue(limit);
            request.NewestFirst = !parseResult.GetValue(oldestFirst);
            return CliOutput.RunAsync(
                () => actions.ListSessions(request, cancellationToken),
                json,
                WriteSessions);
        });
        return command;
    }

    /// <summary>
    /// Creates an event-driven session watch with an exclusive cursor, inactivity timeout, count, and JSON Lines output.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateSessionsWatch(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("watch", "Watch newly completed sessions until timeout, count, or cancellation.");
        var filters = new SessionFilterOptions(includeIdBounds: false);
        var afterId = NullableIntOption("--after-id", "Exclusive session ID cursor; defaults to the current newest session.");
        var timeout = CreateTimeoutOption();
        var count = new Option<int>("--count")
        {
            Description = "Stop after this many matches; zero watches until timeout.",
            DefaultValueFactory = _ => 0
        };
        var jsonl = new Option<bool>("--jsonl") { Description = "Write one JSON object per matched session." };
        filters.AddTo(command);
        AddOptions(command, afterId, timeout, count, jsonl);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var useJsonLines = parseResult.GetValue(jsonOption) || parseResult.GetValue(jsonl);
            var maximum = parseResult.GetValue(count);
            if (maximum < 0)
            {
                return CliOutput.Error(new ArgumentOutOfRangeException("count", "Count cannot be negative."), useJsonLines);
            }

            try
            {
                var written = 0;
                await foreach (var session in actions.WatchSessions(
                    filters.CreateRequest(parseResult),
                    parseResult.GetValue(afterId),
                    parseResult.GetValue(timeout) * 1000,
                    cancellationToken).ConfigureAwait(false))
                {
                    Console.WriteLine(useJsonLines
                        ? JsonSerializer.Serialize(session)
                        : $"{session.Id,6} {session.Method,8} {session.StatusCode?.ToString() ?? "-",6} {session.Url}");
                    written++;
                    if (maximum > 0 && written >= maximum)
                    {
                        break;
                    }
                }

                return ExitCodes.Success;
            }
            catch (Exception exception)
            {
                return CliOutput.Error(exception, useJsonLines);
            }
        });
        return command;
    }

    /// <summary>
    /// Creates the session-detail command for metadata and exact headers.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateSessionsShow(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("show", "Show session metadata and exact headers without bodies.");
        var sessionId = new Argument<int>("session-id") { Description = "Fiddler session ID." };
        command.Arguments.Add(sessionId);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => actions.GetSession(parseResult.GetValue(sessionId), cancellationToken),
                json,
                WriteSessionDetails);
        });
        return command;
    }

    /// <summary>
    /// Creates the body export command with direction validation and stdout isolation.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateSessionsBody(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("body", "Write a complete raw request or response body in chunks.");
        var sessionId = new Argument<int>("session-id") { Description = "Fiddler session ID." };
        var direction = new Option<string>("--direction", "-d")
        {
            Description = "Body direction: request or response.",
            Required = true
        };
        direction.AcceptOnlyFromAmong(BodyDirections.Request, BodyDirections.Response);
        var output = new Option<string>("--output", "-o")
        {
            Description = "Output file path, or '-' for stdout.",
            Required = true
        };
        command.Arguments.Add(sessionId);
        AddOptions(command, direction, output);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var outputPath = parseResult.GetRequiredValue(output);
            try
            {
                var result = await actions.WriteBody(
                    parseResult.GetValue(sessionId),
                    parseResult.GetRequiredValue(direction),
                    outputPath,
                    cancellationToken).ConfigureAwait(false);
                if (outputPath == "-")
                {
                    Console.Error.WriteLine($"Wrote {result.BytesWritten} bytes.");
                }
                else
                {
                    CliOutput.Write(result, json, () => Console.WriteLine($"Wrote {result.BytesWritten} bytes to {result.Path}"));
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

    /// <summary>
    /// Creates the destructive clear command and enforces interactive or explicit confirmation.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateSessionsClear(CliActions actions, Option<bool> jsonOption)
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
    private static Command CreateSessionsRemove(CliActions actions, Option<bool> jsonOption)
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
    private static Command CreateSessionsSave(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("save", "Save sessions to an absolute .saz archive path.");
        var path = new Argument<string>("path") { Description = "Absolute destination .saz path." };
        var ids = new Option<int[]>("--ids")
        {
            Description = "Session IDs to save; omit to save all.",
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
    private static Command CreateSessionsLoad(CliActions actions, Option<bool> jsonOption)
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
    private static Command CreateSessionsReplay(CliActions actions, Option<bool> jsonOption)
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
    private static Command CreateSessionsExport(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("export", "Export sensitive captured evidence as cURL, raw HTTP, or HAR.");
        var sessionId = new Argument<int?>("session-id")
        {
            Description = "Session ID for curl or raw-http; omit for HAR.",
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
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var selectedFormat = parseResult.GetRequiredValue(format);
            var outputPath = parseResult.GetRequiredValue(output);
            var id = parseResult.GetValue(sessionId);
            if (string.Equals(selectedFormat, "har", StringComparison.OrdinalIgnoreCase) == id.HasValue)
            {
                return CliOutput.Error(
                    new ArgumentException("HAR export omits session-id; curl and raw-http require session-id."),
                    json);
            }

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

    /// <summary>
    /// Creates structured session comparison for metadata, headers, timing, and body hashes.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateSessionsDiff(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("diff", "Compare two captured sessions without displaying body content.");
        var left = new Argument<int>("left-session-id");
        var right = new Argument<int>("right-session-id");
        command.Arguments.Add(left);
        command.Arguments.Add(right);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => actions.DiffSessions(parseResult.GetValue(left), parseResult.GetValue(right), cancellationToken),
                json,
                WriteSessionDiff);
        });
        return command;
    }

    /// <summary>
    /// Creates WebSocket frame listing and chunked payload export commands for one tunnel session.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handlers.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateSessionsWebSocket(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("websocket", "Inspect WebSocket frames captured in a session.");
        var sessionId = new Argument<int>("session-id") { Description = "Fiddler WebSocket tunnel session ID." };
        command.Arguments.Add(sessionId);

        var list = new Command("list", "List WebSocket frame metadata without payload bytes.");
        var offset = new Option<int>("--offset") { Description = "Zero-based message offset." };
        var limit = new Option<int>("--limit", "-n")
        {
            Description = "Maximum messages (1-1000).",
            DefaultValueFactory = _ => ProtocolConstants.DefaultSessionLimit
        };
        AddRangeValidator(limit, 1, ProtocolConstants.MaxWebSocketMessageLimit, "Limit");
        AddOptions(list, offset, limit);
        list.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => actions.ListWebSocketMessages(
                    parseResult.GetValue(sessionId),
                    parseResult.GetValue(offset),
                    parseResult.GetValue(limit),
                    cancellationToken),
                json,
                WriteWebSocketMessages);
        });

        var get = new Command("get", "Write a complete WebSocket frame payload in bounded chunks.");
        var messageId = new Argument<int>("message-id") { Description = "Message ID returned by list." };
        var payloadOffset = new Option<long>("--offset") { Description = "Initial payload byte offset." };
        var output = new Option<string>("--output", "-o")
        {
            Description = "Output file path, or '-' for stdout.",
            Required = true
        };
        get.Arguments.Add(messageId);
        AddOptions(get, payloadOffset, output);
        get.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var outputPath = parseResult.GetRequiredValue(output);
            try
            {
                var result = await actions.WriteWebSocketMessage(
                    parseResult.GetValue(sessionId),
                    parseResult.GetValue(messageId),
                    parseResult.GetValue(payloadOffset),
                    outputPath,
                    cancellationToken).ConfigureAwait(false);
                if (outputPath == "-")
                {
                    Console.Error.WriteLine($"Wrote {result.BytesWritten} WebSocket payload bytes.");
                }
                else
                {
                    CliOutput.Write(result, json, () => Console.WriteLine($"Wrote {result.BytesWritten} bytes to {result.Path}"));
                }

                return ExitCodes.Success;
            }
            catch (Exception exception)
            {
                return CliOutput.Error(exception, json);
            }
        });

        command.Subcommands.Add(list);
        command.Subcommands.Add(get);
        return command;
    }

    /// <summary>
    /// Creates the request composition group and binds method, headers, and body inputs.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateRequest(CliActions actions, Option<bool> jsonOption)
    {
        var request = new Command("request", "Compose requests through Fiddler.");
        var send = new Command("send", "Queue an HTTP or HTTPS request through Fiddler.");
        var url = new Argument<string>("url") { Description = "Absolute http or https URL." };
        var method = new Option<string>("--method", "-X")
        {
            Description = "HTTP method.",
            DefaultValueFactory = _ => "GET"
        };
        var headers = new Option<string[]>("--header", "-H")
        {
            Description = "Header in 'Name: value' form; repeat as needed.",
            AllowMultipleArgumentsPerToken = true
        };
        var body = new Option<string?>("--body") { Description = "UTF-8 request body." };
        var bodyFile = new Option<string?>("--body-file") { Description = "Binary request body file, maximum 4 MiB." };
        var wait = new Option<bool>("--wait") { Description = "Wait for the resulting completed session." };
        var timeout = CreateTimeoutOption();
        send.Arguments.Add(url);
        AddOptions(send, method, headers, body, bodyFile, wait, timeout);
        send.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => actions.SendRequest(
                    parseResult.GetRequiredValue(url),
                    parseResult.GetRequiredValue(method),
                    parseResult.GetValue(headers) ?? Array.Empty<string>(),
                    parseResult.GetValue(body),
                    parseResult.GetValue(bodyFile),
                    parseResult.GetValue(wait),
                    parseResult.GetValue(timeout) * 1000,
                    cancellationToken),
                json,
                WriteQueuedResult);
        });
        request.Subcommands.Add(send);
        return request;
    }

    /// <summary>
    /// Creates bridge installation and confirmed uninstallation commands.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handlers.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateBridge(CliActions actions, Option<bool> jsonOption)
    {
        var bridge = new Command("bridge", "Install or remove the Fiddler extension bridge.");
        var install = new Command("install", "Copy bridge assemblies into the current user's Fiddler Scripts directory.");
        install.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(jsonOption);
            try
            {
                var files = actions.InstallBridge();
                CliOutput.Write(new { installed = files }, json, () =>
                {
                    Console.WriteLine("Bridge installed. Restart Fiddler Classic to load it.");
                    foreach (var file in files)
                    {
                        Console.WriteLine(file);
                    }
                });
                return ExitCodes.Success;
            }
            catch (Exception exception)
            {
                return CliOutput.Error(exception, json);
            }
        });

        var uninstall = new Command("uninstall", "Remove bridge assemblies from the current user's Fiddler Scripts directory.");
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm without prompting." };
        uninstall.Options.Add(yes);
        uninstall.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(jsonOption);
            if (!CliOutput.Confirm("Uninstall the Fiddler Classic bridge?", parseResult.GetValue(yes)))
            {
                return ConfirmationRequired(json);
            }

            try
            {
                var files = actions.UninstallBridge();
                CliOutput.Write(new { removed = files }, json, () => Console.WriteLine($"Removed {files.Count} bridge files."));
                return ExitCodes.Success;
            }
            catch (Exception exception)
            {
                return CliOutput.Error(exception, json);
            }
        });

        bridge.Subcommands.Add(install);
        bridge.Subcommands.Add(uninstall);
        return bridge;
    }

    /// <summary>
    /// Creates MCP stdio and authenticated loopback HTTP transport commands.
    /// </summary>
    /// <param name="actions">Provides the persisted HTTP configuration.</param>
    private static Command CreateMcp(CliActions actions)
    {
        var mcp = new Command("mcp", "Run the Fiddler Classic MCP server.");
        var stdio = new Command("stdio", "Run MCP over standard input/output.");
        stdio.SetAction(async (_, cancellationToken) =>
        {
            await McpHost.RunStdioAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        });

        var http = new Command("http", "Run stateless Streamable HTTP MCP on loopback.");
        var port = new Option<int?>("--port", "-p") { Description = "Loopback TCP port; defaults to the configured port (8877)." };
        http.Options.Add(port);
        http.SetAction(async (parseResult, cancellationToken) =>
        {
            var configuration = actions.GetConfiguration();
            var selectedPort = parseResult.GetValue(port) ?? configuration.HttpPort;
            Console.Error.WriteLine($"Fiddler Classic MCP listening on http://127.0.0.1:{selectedPort}/mcp");
            await McpHost.RunHttpAsync(selectedPort, configuration.HttpBearerToken, cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        });

        mcp.Subcommands.Add(stdio);
        mcp.Subcommands.Add(http);
        return mcp;
    }

    /// <summary>
    /// Creates explicit daemon start, status, and stop commands.
    /// </summary>
    /// <param name="daemonClient">The daemon lifecycle client.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateDaemon(DaemonClient daemonClient, Option<bool> jsonOption)
    {
        var daemon = new Command("daemon", "Manage the persistent CLI background process.");

        var start = new Command("start", "Start the CLI daemon if it is not already running.");
        start.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => daemonClient.EnsureStartedAsync(cancellationToken),
                json,
                WriteDaemonStatus);
        });

        var status = new Command("status", "Show CLI daemon process and pipe status.");
        status.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            try
            {
                var result = await daemonClient.TryGetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    CliOutput.Write(new { running = false }, json, () => Console.WriteLine("CLI daemon is not running."));
                    return ExitCodes.Unavailable;
                }

                CliOutput.Write(result, json, () => WriteDaemonStatus(result));
                return ExitCodes.Success;
            }
            catch (Exception exception)
            {
                return CliOutput.Error(exception, json);
            }
        });

        var stop = new Command("stop", "Stop the CLI daemon.");
        stop.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return CliOutput.RunAsync(
                () => daemonClient.StopAsync(cancellationToken),
                json,
                result => Console.WriteLine(result.WasRunning ? "CLI daemon stopped." : "CLI daemon was not running."));
        });

        var run = new Command("run", "Run the CLI daemon in the foreground.") { Hidden = true };
        run.SetAction(async (_, cancellationToken) =>
        {
            await new DaemonServer().RunAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        });

        daemon.Subcommands.Add(start);
        daemon.Subcommands.Add(status);
        daemon.Subcommands.Add(stop);
        daemon.Subcommands.Add(run);
        return daemon;
    }

    /// <summary>
    /// Creates bearer-token display and rotation commands.
    /// </summary>
    /// <param name="actions">Provides configuration reads and token rotation.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateConfig(CliActions actions, Option<bool> jsonOption)
    {
        var config = new Command("config", "Manage local host configuration.");
        var token = new Command("token", "Show or rotate the HTTP MCP bearer token.");
        var show = new Command("show", "Print the HTTP MCP bearer token.");
        show.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(jsonOption);
            var configuration = actions.GetConfiguration();
            CliOutput.Write(
                new { token = configuration.HttpBearerToken },
                json,
                () => Console.WriteLine(configuration.HttpBearerToken));
        });

        var rotate = new Command("rotate", "Generate and store a new HTTP MCP bearer token.");
        rotate.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(jsonOption);
            var configuration = actions.RotateToken();
            CliOutput.Write(
                new { token = configuration.HttpBearerToken },
                json,
                () => Console.WriteLine(configuration.HttpBearerToken));
        });

        token.Subcommands.Add(show);
        token.Subcommands.Add(rotate);
        config.Subcommands.Add(token);
        return config;
    }

    private static int ConfirmationRequired(bool json)
    {
        return CliOutput.Error(
            new Bridge.BridgeClientException(ErrorCodes.ConfirmationRequired, "Confirmation is required. Use --yes in non-interactive use."),
            json);
    }

    private static Option<int?> NullableIntOption(string name, string description)
    {
        return new Option<int?>(name) { Description = description };
    }

    private static Option<string?> NullableStringOption(string name, string description)
    {
        return new Option<string?>(name) { Description = description };
    }

    private static Option<int> CreateTimeoutOption()
    {
        var option = new Option<int>("--timeout")
        {
            Description = "Wait or inactivity timeout in seconds (1-60).",
            DefaultValueFactory = _ => ProtocolConstants.DefaultWaitMilliseconds / 1000
        };
        option.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int>();
            if (value < 1 || value > ProtocolConstants.MaxWaitMilliseconds / 1000)
            {
                result.AddError("Timeout must be between 1 and 60 seconds.");
            }
        });
        return option;
    }

    private static ListSessionsRequest CreateExportFilters(ListSessionsRequest filters, int limit)
    {
        filters.Limit = limit;
        filters.NewestFirst = false;
        return filters;
    }

    private static void AddOptions(Command command, params Option[] options)
    {
        foreach (var option in options)
        {
            command.Options.Add(option);
        }
    }

    private static void AddRangeValidator(Option<int> option, int minimum, int maximum, string label)
    {
        option.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int>();
            if (value < minimum || value > maximum)
            {
                result.AddError($"{label} must be between {minimum} and {maximum}.");
            }
        });
    }

    /// <summary>
    /// Writes one aligned line for each diagnostic check.
    /// </summary>
    /// <param name="result">The completed diagnostic report.</param>
    private static void WriteDoctor(DoctorResult result)
    {
        foreach (var check in result.Checks)
        {
            Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL"),-4} {check.Name,-20} {check.Details}");
        }
    }

    /// <summary>
    /// Writes the human-readable Fiddler, bridge, proxy, and session status view.
    /// </summary>
    /// <param name="status">The combined status response.</param>
    private static void WriteStatus(StatusResponse status)
    {
        Console.WriteLine($"Fiddler installed: {status.FiddlerInstalled} {status.FiddlerVersion}");
        Console.WriteLine($"Fiddler running:   {status.FiddlerRunning} {(status.FiddlerProcessId.HasValue ? $"PID {status.FiddlerProcessId}" : string.Empty)}");
        Console.WriteLine($"Bridge installed:  {status.BridgeInstalled}");
        Console.WriteLine($"Bridge connected:  {status.BridgeConnected}");
        Console.WriteLine($"Capture active:    {status.IsProxyAttached}");
        Console.WriteLine($"Proxy listening:   {status.IsListening} {(status.ListenPort.HasValue ? $"port {status.ListenPort}" : string.Empty)}");
        Console.WriteLine($"HTTPS decryption:  {status.IsHttpsDecryptionEnabled}");
        Console.WriteLine($"Sessions:          {status.SessionCount} ({status.CompletedSessionCount} complete)");
    }

    /// <summary>
    /// Writes daemon identity, process, pipe, and version details.
    /// </summary>
    /// <param name="status">The daemon status response.</param>
    private static void WriteDaemonStatus(DaemonStatus status)
    {
        Console.WriteLine($"CLI daemon:        {(status.Running ? "running" : "stopped")}");
        Console.WriteLine($"Process:           PID {status.ProcessId}");
        Console.WriteLine($"Started:           {status.StartedAtUtc}");
        Console.WriteLine($"Pipe:              {status.PipeName}");
        Console.WriteLine($"Host version:      {status.HostVersion}");
    }

    /// <summary>
    /// Writes a bounded tabular session summary with compact URLs.
    /// </summary>
    /// <param name="result">The filtered session-list response.</param>
    private static void WriteSessions(ListSessionsResponse result)
    {
        Console.WriteLine($"Matched {result.TotalMatched}; showing {result.Sessions.Count}");
        Console.WriteLine($"{"ID",6} {"METHOD",8} {"STATUS",6} {"PROCESS",18} URL");
        foreach (var session in result.Sessions)
        {
            var url = session.Url.Length > 100 ? session.Url[..97] + "..." : session.Url;
            Console.WriteLine($"{session.Id,6} {session.Method,8} {session.StatusCode?.ToString() ?? "-",6} {session.Process ?? "-",18} {url}");
        }
    }

    private static void WriteQueuedResult(CliQueuedResult result)
    {
        Console.WriteLine(result.Queued.Message);
        if (result.CapturedSession != null)
        {
            Console.WriteLine($"Captured as session {result.CapturedSession.Id}: {result.CapturedSession.StatusCode} {result.CapturedSession.Url}");
        }
        else if (result.TimedOut)
        {
            Console.WriteLine("The request was accepted, but no matching completed session arrived before timeout.");
        }
    }

    private static void WriteSessionDiff(SessionDiffResponse result)
    {
        Console.WriteLine($"Compared sessions {result.LeftSessionId} and {result.RightSessionId}: {result.Differences.Count} difference(s)");
        foreach (var difference in result.Differences)
        {
            Console.WriteLine($"{difference.Area}/{difference.Name}");
            Console.WriteLine($"  left:  {string.Join(" | ", difference.LeftValues)}");
            Console.WriteLine($"  right: {string.Join(" | ", difference.RightValues)}");
        }
    }

    private static void WriteWebSocketMessages(ListWebSocketMessagesResponse result)
    {
        Console.WriteLine($"Session {result.SessionId}: {result.TotalMessages} WebSocket message(s)");
        Console.WriteLine($"{"ID",6} {"DIRECTION",9} {"OPCODE",12} {"BYTES",10} {"FINAL",5} TIMESTAMP");
        foreach (var message in result.Messages)
        {
            Console.WriteLine($"{message.MessageId,6} {message.Direction,9} {message.Opcode,12} {message.PayloadLength,10} {message.IsFinal,5} {message.TimestampUtc}");
        }
    }

    /// <summary>
    /// Writes one session's metadata followed by its exact request and response headers.
    /// </summary>
    /// <param name="details">The session detail response.</param>
    private static void WriteSessionDetails(SessionDetails details)
    {
        var session = details.Summary;
        Console.WriteLine($"Session: {session.Id} ({session.State})");
        Console.WriteLine($"Request: {session.Method} {session.Url}");
        Console.WriteLine($"Response: {session.StatusCode} {session.StatusDescription}");
        Console.WriteLine($"Process: {session.Process} ({session.ProcessId})");
        Console.WriteLine($"Bodies: request={session.RequestBodyBytes} bytes response={session.ResponseBodyBytes} bytes");
        WriteHeaders("Request headers", details.RequestHeaders);
        WriteHeaders("Response headers", details.ResponseHeaders);
    }

    /// <summary>
    /// Writes an ordered header collection without redaction or normalization.
    /// </summary>
    /// <param name="title">The heading for the header collection.</param>
    /// <param name="headers">The optional ordered headers.</param>
    private static void WriteHeaders(string title, IEnumerable<HeaderDto>? headers)
    {
        Console.WriteLine();
        Console.WriteLine(title + ":");
        foreach (var header in headers ?? Array.Empty<HeaderDto>())
        {
            Console.WriteLine($"{header.Name}: {header.Value}");
        }
    }

    /// <summary>
    /// Owns the reusable CLI filter options and maps them into the shared protocol request.
    /// </summary>
    private sealed class SessionFilterOptions
    {
        private readonly bool _includeIdBounds;
        private readonly Option<int?> _minId = NullableIntOption("--min-id", "Minimum session ID, inclusive.");
        private readonly Option<int?> _maxId = NullableIntOption("--max-id", "Maximum session ID, inclusive.");
        private readonly Option<string?> _method = NullableStringOption("--method", "Exact HTTP method.");
        private readonly Option<string?> _host = NullableStringOption("--host", "Host substring.");
        private readonly Option<string?> _url = NullableStringOption("--url", "Full URL substring.");
        private readonly Option<int?> _status = NullableIntOption("--status", "Exact status code.");
        private readonly Option<string?> _contentType = NullableStringOption("--content-type", "Response content-type substring.");
        private readonly Option<string?> _process = NullableStringOption("--process", "Client process substring.");
        private readonly Option<string?> _headerName = NullableStringOption("--header-name", "Exact request or response header name.");
        private readonly Option<string?> _headerValue = NullableStringOption("--header-value", "Request or response header value substring.");
        private readonly Option<double?> _minDuration = new("--min-duration-ms") { Description = "Minimum duration in milliseconds." };
        private readonly Option<double?> _maxDuration = new("--max-duration-ms") { Description = "Maximum duration in milliseconds." };
        private readonly Option<string?> _protocol = NullableStringOption("--protocol", "Exact HTTP protocol, such as HTTP/1.1.");
        private readonly Option<long?> _minBodyBytes = new("--min-body-bytes") { Description = "Minimum combined body bytes." };
        private readonly Option<long?> _maxBodyBytes = new("--max-body-bytes") { Description = "Maximum combined body bytes." };
        private readonly Option<bool> _errorsOnly = new("--errors-only") { Description = "Include only aborted or HTTP 400+ sessions." };
        private readonly Option<bool> _successfulOnly = new("--successful-only") { Description = "Exclude aborted and HTTP 400+ sessions." };
        private readonly Option<string?> _bodyContains = NullableStringOption("--body-contains", "Exact UTF-8 bytes in a bounded body prefix.");
        private readonly Option<string> _bodyDirection = new("--body-direction")
        {
            Description = "Body search direction: request or response.",
            DefaultValueFactory = _ => BodyDirections.Response
        };
        private readonly Option<int> _bodySearchBytes = new("--body-search-bytes")
        {
            Description = "Maximum body prefix bytes searched per session (1-1048576).",
            DefaultValueFactory = _ => ProtocolConstants.DefaultBodySearchBytes
        };

        public SessionFilterOptions(bool includeIdBounds = true)
        {
            _includeIdBounds = includeIdBounds;
            _bodyDirection.AcceptOnlyFromAmong(BodyDirections.Request, BodyDirections.Response);
        }

        /// <summary>
        /// Adds the complete filter option set and its mutually exclusive error-mode validator.
        /// </summary>
        /// <param name="command">The list, watch, or HAR export command.</param>
        public void AddTo(Command command)
        {
            if (_includeIdBounds)
            {
                AddOptions(command, _minId, _maxId);
            }

            AddOptions(
                command,
                _method,
                _host,
                _url,
                _status,
                _contentType,
                _process,
                _headerName,
                _headerValue,
                _minDuration,
                _maxDuration,
                _protocol,
                _minBodyBytes,
                _maxBodyBytes,
                _errorsOnly,
                _successfulOnly,
                _bodyContains,
                _bodyDirection,
                _bodySearchBytes);
            command.Validators.Add(result =>
            {
                if (result.GetValue(_errorsOnly) && result.GetValue(_successfulOnly))
                {
                    result.AddError("--errors-only and --successful-only cannot be combined.");
                }

                var minId = result.GetValue(_minId);
                var maxId = result.GetValue(_maxId);
                if (_includeIdBounds && (minId < 0 || maxId < 0 || (minId.HasValue && maxId.HasValue && minId > maxId)))
                {
                    result.AddError("Session ID bounds are invalid.");
                }

                var minDuration = result.GetValue(_minDuration);
                var maxDuration = result.GetValue(_maxDuration);
                if (minDuration < 0 || maxDuration < 0
                    || (minDuration.HasValue && maxDuration.HasValue && minDuration > maxDuration))
                {
                    result.AddError("Duration bounds are invalid.");
                }

                var minBody = result.GetValue(_minBodyBytes);
                var maxBody = result.GetValue(_maxBodyBytes);
                if (minBody < 0 || maxBody < 0 || (minBody.HasValue && maxBody.HasValue && minBody > maxBody))
                {
                    result.AddError("Body-size bounds are invalid.");
                }

                var bodySearchBytes = result.GetValue(_bodySearchBytes);
                if (bodySearchBytes != 0
                    && (bodySearchBytes < 1 || bodySearchBytes > ProtocolConstants.MaxBodySearchBytes))
                {
                    result.AddError($"Body search bytes must be between 1 and {ProtocolConstants.MaxBodySearchBytes}.");
                }
            });
        }

        /// <summary>
        /// Maps parsed CLI values to the bridge's reusable filter contract.
        /// </summary>
        /// <param name="parseResult">The command parse result containing this option set.</param>
        public ListSessionsRequest CreateRequest(System.CommandLine.ParseResult parseResult)
        {
            var errorsOnly = parseResult.GetValue(_errorsOnly);
            var successfulOnly = parseResult.GetValue(_successfulOnly);
            return new ListSessionsRequest
            {
                MinId = _includeIdBounds ? parseResult.GetValue(_minId) : null,
                MaxId = _includeIdBounds ? parseResult.GetValue(_maxId) : null,
                Method = parseResult.GetValue(_method),
                Host = parseResult.GetValue(_host),
                UrlContains = parseResult.GetValue(_url),
                StatusCode = parseResult.GetValue(_status),
                ContentType = parseResult.GetValue(_contentType),
                Process = parseResult.GetValue(_process),
                HeaderName = parseResult.GetValue(_headerName),
                HeaderValue = parseResult.GetValue(_headerValue),
                MinDurationMilliseconds = parseResult.GetValue(_minDuration),
                MaxDurationMilliseconds = parseResult.GetValue(_maxDuration),
                Protocol = parseResult.GetValue(_protocol),
                MinBodyBytes = parseResult.GetValue(_minBodyBytes),
                MaxBodyBytes = parseResult.GetValue(_maxBodyBytes),
                IsError = errorsOnly ? true : successfulOnly ? false : null,
                BodyContains = parseResult.GetValue(_bodyContains),
                BodyDirection = parseResult.GetRequiredValue(_bodyDirection),
                BodySearchBytes = parseResult.GetValue(_bodySearchBytes)
            };
        }
    }
}
