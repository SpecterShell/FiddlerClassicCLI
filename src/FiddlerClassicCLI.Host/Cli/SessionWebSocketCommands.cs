// Builds WebSocket frame inspection and bounded payload export commands.
using System.CommandLine;
using FiddlerClassicCLI.Protocol;
using static FiddlerClassicCLI.Host.Cli.CommandHelpers;

namespace FiddlerClassicCLI.Host.Cli;

internal static class SessionWebSocketCommands
{
    /// <summary>
    /// Creates WebSocket frame listing and chunked payload export commands for one tunnel session.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handlers.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command Create(CliActions actions, Option<bool> jsonOption)
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

    private static void WriteWebSocketMessages(ListWebSocketMessagesResponse result)
    {
        Console.WriteLine($"Session {result.SessionId}: {result.TotalMessages} WebSocket message(s)");
        Console.WriteLine($"{"ID",6} {"DIRECTION",9} {"OPCODE",12} {"BYTES",10} {"FINAL",5} TIMESTAMP");
        foreach (var message in result.Messages)
        {
            Console.WriteLine($"{message.MessageId,6} {message.Direction,9} {message.Opcode,12} {message.PayloadLength,10} {message.IsFinal,5} {message.TimestampUtc}");
        }
    }
}
