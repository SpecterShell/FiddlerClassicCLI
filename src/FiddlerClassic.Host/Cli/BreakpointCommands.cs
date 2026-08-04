// Builds managed request and response breakpoint lifecycle and mutation CLI commands.
using System.CommandLine;
using System.Text;
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Host.Mcp;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Cli;

internal static class BreakpointCommands
{
    /// <summary>
    /// Creates the complete breakpoint command tree.
    /// </summary>
    /// <param name="actions">The shared CLI operation facade.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command Create(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("breakpoints", "Pause, inspect, mutate, resume, or abort Fiddler sessions.");
        command.Subcommands.Add(CreateStatus(actions, jsonOption));
        command.Subcommands.Add(CreateArms(actions, jsonOption));
        command.Subcommands.Add(CreateArm(actions, jsonOption));
        command.Subcommands.Add(CreateDisarm(actions, jsonOption));
        command.Subcommands.Add(CreateList(actions, jsonOption));
        command.Subcommands.Add(CreateWait(actions, jsonOption));
        command.Subcommands.Add(CreateShow(actions, jsonOption));
        command.Subcommands.Add(CreateUpdate(actions, jsonOption));
        command.Subcommands.Add(CreateResume(actions, jsonOption));
        command.Subcommands.Add(CreateAbort(actions, jsonOption));
        return command;
    }

    private static Command CreateStatus(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("status", "Show active arms and currently paused sessions.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            try
            {
                var arms = await actions.ListBreakpointArms(cancellationToken).ConfigureAwait(false);
                var pending = await actions.ListPendingBreakpoints(new ListPendingBreakpointsRequest(), cancellationToken).ConfigureAwait(false);
                var result = new BreakpointStatusResult { Arms = arms.Arms, Pending = pending.Breakpoints, LatestSequence = pending.LatestSequence };
                CliOutput.Write(
                    result,
                    json,
                    () => Console.WriteLine($"Arms: {result.Arms.Count}; pending: {result.Pending.Count}; latest sequence: {result.LatestSequence}"));
                return ExitCodes.Success;
            }
            catch (Exception exception)
            {
                return CliOutput.Error(exception, json);
            }
        });
        return command;
    }

    private static Command CreateArms(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("arms", "List active breakpoint arms.");
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.ListBreakpointArms(cancellationToken),
            parseResult.GetValue(jsonOption),
            result =>
            {
                Console.WriteLine($"Arms: {result.Arms.Count}");
                foreach (var arm in result.Arms)
                {
                    Console.WriteLine($"{arm.ArmId} {arm.Stage} {(arm.OneShot ? "one-shot" : "persistent")} hold={arm.HoldMilliseconds}ms {DescribeFilter(arm.Filter)}");
                }
            }));
        return command;
    }

    /// <summary>
    /// Creates request/response arms with shared metadata and stage-aware filters.
    /// </summary>
    /// <param name="actions">The shared CLI operation facade.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateArm(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("arm", "Arm a request or response breakpoint for future matching traffic.");
        var stage = new Argument<string>("stage") { Description = "Breakpoint stage: request or response." };
        stage.AcceptOnlyFromAmong(BreakpointStages.Request, BreakpointStages.Response);
        var method = TextOption("--method", "Exact HTTP method.");
        var host = TextOption("--host", "Host substring.");
        var url = TextOption("--url", "Full URL substring.");
        var status = new Option<int?>("--status") { Description = "Exact response status code." };
        var contentType = TextOption("--content-type", "Response content-type substring.");
        var process = TextOption("--process", "Client process substring.");
        var headerName = TextOption("--header-name", "Exact stage header name.");
        var headerValue = TextOption("--header-value", "Stage header value substring.");
        var persistent = Flag("--persistent", "Keep the arm after it first matches.");
        var hold = new Option<int>("--hold")
        {
            Description = "Automatic hold timeout in seconds (1-300).",
            DefaultValueFactory = _ => ProtocolConstants.DefaultBreakpointHoldMilliseconds / 1000
        };
        hold.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int>();
            if (value < 1 || value > ProtocolConstants.MaxBreakpointHoldMilliseconds / 1000)
            {
                result.AddError("Hold must be between 1 and 300 seconds.");
            }
        });
        command.Arguments.Add(stage);
        AddOptions(command, method, host, url, status, contentType, process, headerName, headerValue, persistent, hold);
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.ArmBreakpoint(
                new ArmBreakpointRequest
                {
                    Stage = parseResult.GetRequiredValue(stage),
                    Filter = new BreakpointFilterDto
                    {
                        Method = parseResult.GetValue(method),
                        Host = parseResult.GetValue(host),
                        UrlContains = parseResult.GetValue(url),
                        StatusCode = parseResult.GetValue(status),
                        ContentType = parseResult.GetValue(contentType),
                        Process = parseResult.GetValue(process),
                        HeaderName = parseResult.GetValue(headerName),
                        HeaderValue = parseResult.GetValue(headerValue)
                    },
                    OneShot = !parseResult.GetValue(persistent),
                    HoldMilliseconds = checked(parseResult.GetValue(hold) * 1000)
                },
                cancellationToken),
            parseResult.GetValue(jsonOption),
            result => Console.WriteLine($"Armed {result.Arm!.ArmId} for {result.Arm.Stage} breakpoints.")));
        return command;
    }

    private static Command CreateDisarm(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("disarm", "Remove an arm without resuming sessions already paused.");
        var armId = new Argument<string>("arm-id");
        command.Arguments.Add(armId);
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.DisarmBreakpoint(parseResult.GetRequiredValue(armId), cancellationToken),
            parseResult.GetValue(jsonOption),
            result => Console.WriteLine(result.Removed ? $"Disarmed {result.Arm?.ArmId}." : "No arm was removed.")));
        return command;
    }

    private static Command CreateList(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("list", "List currently paused breakpoints.");
        var stage = TextOption("--stage", "Breakpoint stage: request or response.");
        stage.AcceptOnlyFromAmong(BreakpointStages.Request, BreakpointStages.Response);
        var sessionId = new Option<int?>("--session-id") { Description = "Exact Fiddler session ID." };
        AddOptions(command, stage, sessionId);
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.ListPendingBreakpoints(
                new ListPendingBreakpointsRequest
                {
                    Stage = parseResult.GetValue(stage),
                    SessionId = parseResult.GetValue(sessionId)
                },
                cancellationToken),
            parseResult.GetValue(jsonOption),
            WritePending));
        return command;
    }

    private static Command CreateWait(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("wait", "Wait for a currently paused breakpoint after a sequence cursor.");
        var after = new Option<long>("--after-sequence") { Description = "Exclusive breakpoint sequence cursor.", DefaultValueFactory = _ => 0 };
        var timeout = new Option<int>("--timeout")
        {
            Description = "Wait timeout in seconds (1-60).",
            DefaultValueFactory = _ => ProtocolConstants.DefaultWaitMilliseconds / 1000
        };
        timeout.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int>();
            if (value < 1 || value > ProtocolConstants.MaxWaitMilliseconds / 1000)
            {
                result.AddError("Timeout must be between 1 and 60 seconds.");
            }
        });
        var stage = TextOption("--stage", "Breakpoint stage: request or response.");
        stage.AcceptOnlyFromAmong(BreakpointStages.Request, BreakpointStages.Response);
        var sessionId = new Option<int?>("--session-id") { Description = "Exact Fiddler session ID." };
        AddOptions(command, after, timeout, stage, sessionId);
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.WaitForBreakpoint(
                new WaitForBreakpointRequest
                {
                    AfterSequence = parseResult.GetValue(after),
                    TimeoutMilliseconds = checked(parseResult.GetValue(timeout) * 1000),
                    Stage = parseResult.GetValue(stage),
                    SessionId = parseResult.GetValue(sessionId)
                },
                cancellationToken),
            parseResult.GetValue(jsonOption),
            result =>
            {
                if (!result.Matched || result.Breakpoint == null)
                {
                    Console.WriteLine($"No breakpoint matched before timeout. Latest sequence: {result.LatestSequence}");
                }
                else
                {
                    WritePendingLine(result.Breakpoint);
                }
            }));
        return command;
    }

    private static Command CreateShow(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("show", "Show paused request metadata and exact headers without bodies.");
        var breakpointId = new Argument<string>("breakpoint-id");
        command.Arguments.Add(breakpointId);
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.GetBreakpoint(parseResult.GetRequiredValue(breakpointId), includeHeaders: true, cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteDetails));
        return command;
    }

    /// <summary>
    /// Creates a stage-checked mutation command with exact header operations and bounded body input.
    /// </summary>
    /// <param name="actions">The shared CLI operation facade.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateUpdate(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("update", "Mutate a currently paused request or response.");
        var breakpointId = new Argument<string>("breakpoint-id");
        var method = TextOption("--method", "Replacement request method.");
        var url = TextOption("--url", "Replacement absolute request URL.");
        var status = new Option<int?>("--status") { Description = "Replacement response status code." };
        var reason = TextOption("--reason", "Replacement response status description.");
        var setHeaders = new Option<string[]>("--set-header", "-H")
        {
            Description = "Set a stage header in 'Name: value' form; repeat as needed.",
            AllowMultipleArgumentsPerToken = true
        };
        var removeHeaders = new Option<string[]>("--remove-header")
        {
            Description = "Remove a stage header by name; repeat as needed.",
            AllowMultipleArgumentsPerToken = true
        };
        var body = TextOption("--body", "Replacement UTF-8 body, including an empty string.");
        var bodyFile = TextOption("--body-file", "Replacement binary body file, maximum 4 MiB.");
        command.Arguments.Add(breakpointId);
        AddOptions(command, method, url, status, reason, setHeaders, removeHeaders, body, bodyFile);
        command.Validators.Add(result =>
        {
            if (result.GetValue(body) != null && result.GetValue(bodyFile) != null)
            {
                result.AddError("--body and --body-file cannot be combined.");
            }
        });
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            try
            {
                var bodyBase64 = ReadBody(parseResult.GetValue(body), parseResult.GetValue(bodyFile));
                return CliOutput.RunAsync(
                    () => actions.UpdateBreakpoint(
                        new UpdateBreakpointRequest
                        {
                            BreakpointId = parseResult.GetRequiredValue(breakpointId),
                            Method = parseResult.GetValue(method),
                            Url = parseResult.GetValue(url),
                            StatusCode = parseResult.GetValue(status),
                            StatusDescription = parseResult.GetValue(reason),
                            SetHeaders = RequestInput.ParseHeaders(parseResult.GetValue(setHeaders) ?? Array.Empty<string>()),
                            RemoveHeaders = parseResult.GetValue(removeHeaders) ?? Array.Empty<string>(),
                            BodyBase64 = bodyBase64
                        },
                        cancellationToken),
                    json,
                    WriteDetails);
            }
            catch (Exception exception)
            {
                return Task.FromResult(CliOutput.Error(exception, json));
            }
        });
        return command;
    }

    private static Command CreateResume(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("resume", "Resume one currently paused session.");
        var breakpointId = new Argument<string>("breakpoint-id");
        command.Arguments.Add(breakpointId);
        command.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => actions.ResumeBreakpoint(parseResult.GetRequiredValue(breakpointId), cancellationToken),
            parseResult.GetValue(jsonOption),
            result => Console.WriteLine($"Session {result.SessionId} resumed.")));
        return command;
    }

    private static Command CreateAbort(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("abort", "Abort one currently paused session.");
        var breakpointId = new Argument<string>("breakpoint-id");
        var yes = Flag("--yes", "Confirm abort without prompting.", "-y");
        command.Arguments.Add(breakpointId);
        command.Options.Add(yes);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            if (!CliOutput.Confirm("Abort this paused network session?", parseResult.GetValue(yes)))
            {
                return Task.FromResult(ConfirmationRequired(json));
            }

            return CliOutput.RunAsync(
                () => actions.AbortBreakpoint(parseResult.GetRequiredValue(breakpointId), cancellationToken),
                json,
                result => Console.WriteLine($"Session {result.SessionId} aborted."));
        });
        return command;
    }

    private static string? ReadBody(string? body, string? bodyFile)
    {
        if (body != null)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        }

        if (bodyFile == null)
        {
            return null;
        }

        var file = new FileInfo(Path.GetFullPath(bodyFile));
        if (!file.Exists)
        {
            throw new FileNotFoundException("The breakpoint body file was not found.", file.FullName);
        }

        if (file.Length > ProtocolConstants.MaxComposeBodyBytes)
        {
            throw new ArgumentException($"The breakpoint body cannot exceed {ProtocolConstants.MaxComposeBodyBytes} bytes.");
        }

        return Convert.ToBase64String(File.ReadAllBytes(file.FullName));
    }

    private static void WritePending(ListPendingBreakpointsResponse result)
    {
        Console.WriteLine($"Pending: {result.Breakpoints.Count}; latest sequence: {result.LatestSequence}");
        foreach (var breakpoint in result.Breakpoints)
        {
            WritePendingLine(breakpoint);
        }
    }

    private static void WritePendingLine(PendingBreakpointDto breakpoint)
    {
        Console.WriteLine($"{breakpoint.Sequence,6} {breakpoint.BreakpointId} session={breakpoint.SessionId} {breakpoint.Stage} {breakpoint.Method} {breakpoint.Url}");
    }

    private static void WriteDetails(BreakpointDetailsResponse result)
    {
        WritePendingLine(result.Breakpoint);
        Console.WriteLine($"State: {result.Breakpoint.State}; managed: {result.Breakpoint.IsManaged}; expires: {result.Breakpoint.ExpiresAtUtc ?? "manual"}");
        Console.WriteLine($"Bodies: request={result.RequestBodyBytes} bytes response={result.ResponseBodyBytes} bytes");
        WriteHeaders("Request headers", result.RequestHeaders);
        WriteHeaders("Response headers", result.ResponseHeaders);
    }

    private static void WriteHeaders(string title, IEnumerable<HeaderDto>? headers)
    {
        Console.WriteLine();
        Console.WriteLine(title + ":");
        foreach (var header in headers ?? Array.Empty<HeaderDto>())
        {
            Console.WriteLine($"{header.Name}: {header.Value}");
        }
    }

    private static string DescribeFilter(BreakpointFilterDto filter)
    {
        var parts = new[]
        {
            filter.Method == null ? null : $"method={filter.Method}",
            filter.Host == null ? null : $"host={filter.Host}",
            filter.UrlContains == null ? null : $"url={filter.UrlContains}",
            filter.StatusCode == null ? null : $"status={filter.StatusCode}",
            filter.ContentType == null ? null : $"content-type={filter.ContentType}",
            filter.Process == null ? null : $"process={filter.Process}",
            filter.HeaderName == null ? null : $"header={filter.HeaderName}"
        };
        return string.Join(" ", parts.Where(part => part != null));
    }

    private static Option<string?> TextOption(string name, string description)
    {
        return new Option<string?>(name) { Description = description };
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

    private static int ConfirmationRequired(bool json)
    {
        return CliOutput.Error(
            new BridgeClientException(ErrorCodes.ConfirmationRequired, "Confirmation is required. Use --yes in non-interactive use."),
            json);
    }

    private sealed class BreakpointStatusResult
    {
        public List<BreakpointArmDto> Arms { get; set; } = new();
        public List<PendingBreakpointDto> Pending { get; set; } = new();
        public long LatestSequence { get; set; }
    }
}
