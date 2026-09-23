// Builds session inspection commands and composes the session command group.
using System.CommandLine;
using System.Text.Json;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;
using static FiddlerClassicCLI.Host.Cli.CommandHelpers;

namespace FiddlerClassicCLI.Host.Cli;

internal static class SessionCommands
{
    /// <summary>
    /// Creates the captured-session command group and its inspection and mutation subcommands.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handlers.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command Create(CliActions actions, Option<bool> jsonOption)
    {
        var sessions = new Command("sessions", "Inspect and manage captured Fiddler sessions.");
        sessions.Subcommands.Add(CreateList(actions, jsonOption));
        sessions.Subcommands.Add(CreateWatch(actions, jsonOption));
        sessions.Subcommands.Add(CreateShow(actions, jsonOption));
        sessions.Subcommands.Add(CreateBody(actions, jsonOption));
        sessions.Subcommands.Add(SessionActionCommands.CreateClear(actions, jsonOption));
        sessions.Subcommands.Add(SessionActionCommands.CreateRemove(actions, jsonOption));
        sessions.Subcommands.Add(SessionActionCommands.CreateSave(actions, jsonOption));
        sessions.Subcommands.Add(SessionActionCommands.CreateLoad(actions, jsonOption));
        sessions.Subcommands.Add(SessionActionCommands.CreateReplay(actions, jsonOption));
        sessions.Subcommands.Add(SessionActionCommands.CreateExport(actions, jsonOption));
        sessions.Subcommands.Add(CreateDiff(actions, jsonOption));
        sessions.Subcommands.Add(SessionWebSocketCommands.Create(actions, jsonOption));
        return sessions;
    }

    /// <summary>
    /// Creates the bounded session-list command and maps every filter option into a bridge request.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateList(CliActions actions, Option<bool> jsonOption)
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
        var summary = new Option<bool>("--summary") { Description = "Aggregate the bounded metadata page by host, status, captured body bytes, and timing." };

        filters.AddTo(command);
        AddOptions(command, limit, oldestFirst, summary);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            var request = filters.CreateRequest(parseResult);
            request.Limit = parseResult.GetValue(limit);
            request.NewestFirst = !parseResult.GetValue(oldestFirst);
            if (parseResult.GetValue(summary))
            {
                return CliOutput.RunAsync(async () =>
                {
                    try
                    {
                        return await actions.SummarizeSessions(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ArgumentException exception)
                    {
                        throw new BridgeClientException(ErrorCodes.InvalidRequest, exception.Message);
                    }
                }, json, WriteSummary);
            }
            return CliOutput.RunAsync(
                () => actions.ListSessions(request, cancellationToken),
                json,
                WriteSessions);
        });
        return command;
    }

    /// <summary>Prints page scope before aggregates so truncation remains visible.</summary>
    /// <param name="result">The metadata-only aggregates for returned sessions.</param>
    private static void WriteSummary(SessionSummaryResult result)
    {
        Console.WriteLine($"Returned {result.Returned} of {result.TotalMatched} matches (limit {result.Limit}, truncated: {result.Truncated}).");
        Console.WriteLine($"Captured body bytes: request {result.RequestBodyBytes}, response {result.ResponseBodyBytes}.");
        foreach (var host in result.ByHost) Console.WriteLine($"Host {host.Host}: {host.Count}");
        foreach (var status in result.ByStatus) Console.WriteLine($"Status {status.StatusCode?.ToString() ?? "unknown"}: {status.Count}");
        var timing = result.DurationMilliseconds;
        Console.WriteLine($"Completed timing samples: {timing.Count}, ms min {timing.Min?.ToString() ?? "-"}, median {timing.Median?.ToString() ?? "-"}, p95 {timing.P95?.ToString() ?? "-"}, max {timing.Max?.ToString() ?? "-"}.");
    }

    /// <summary>
    /// Creates an event-driven session watch with an exclusive cursor, inactivity timeout, count, and JSON Lines output.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateWatch(CliActions actions, Option<bool> jsonOption)
    {
        var command = new Command("watch", "Watch newly completed sessions until timeout, count, or cancellation.");
        var filters = new SessionFilterOptions(includeIdBounds: false);
        var afterId = NullableIntOption("--after-id", "Exclusive session ID cursor. Defaults to the current newest session.");
        var timeout = CreateTimeoutOption();
        var count = new Option<int>("--count")
        {
            Description = "Stop after this many matches. Zero watches until timeout.",
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
    private static Command CreateShow(CliActions actions, Option<bool> jsonOption)
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
    private static Command CreateBody(CliActions actions, Option<bool> jsonOption)
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
    /// Creates structured session comparison for metadata, headers, timing, and body hashes.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateDiff(CliActions actions, Option<bool> jsonOption)
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
    /// Writes a bounded tabular session summary with compact URLs.
    /// </summary>
    /// <param name="result">The filtered session-list response.</param>
    private static void WriteSessions(ListSessionsResponse result)
    {
        Console.WriteLine($"Matched {result.TotalMatched}, showing {result.Sessions.Count}");
        Console.WriteLine($"{"ID",6} {"METHOD",8} {"STATUS",6} {"PROCESS",18} URL");
        foreach (var session in result.Sessions)
        {
            var url = session.Url.Length > 100 ? session.Url[..97] + "..." : session.Url;
            Console.WriteLine($"{session.Id,6} {session.Method,8} {session.StatusCode?.ToString() ?? "-",6} {session.Process ?? "-",18} {url}");
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
}
