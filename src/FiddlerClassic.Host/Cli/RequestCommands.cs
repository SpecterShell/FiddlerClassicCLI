// Builds request composition commands using the shared CLI operation facade.
using System.CommandLine;
using static FiddlerClassic.Host.Cli.CommandHelpers;

namespace FiddlerClassic.Host.Cli;

internal static class RequestCommands
{
    /// <summary>
    /// Creates the request composition group and binds method, headers, and body inputs.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command Create(CliActions actions, Option<bool> jsonOption)
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
}
