// Shares CLI option validation, confirmation errors, and queued-request output.
using System.CommandLine;
using System.Globalization;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Cli;

internal static class CommandHelpers
{
    public static int ConfirmationRequired(bool json)
    {
        return CliOutput.Error(
            new Bridge.BridgeClientException(ErrorCodes.ConfirmationRequired, "Confirmation is required. Use --yes in non-interactive use."),
            json);
    }

    public static Option<int?> NullableIntOption(string name, string description)
    {
        return new Option<int?>(name) { Description = description };
    }

    public static Option<string?> NullableStringOption(string name, string description)
    {
        return new Option<string?>(name) { Description = description };
    }

    /// <summary>
    /// Creates the shared bounded timeout for waits, including application shutdown.
    /// </summary>
    public static Option<int> CreateTimeoutOption()
    {
        var option = new Option<int>("--timeout")
        {
            Description = "Wait or inactivity timeout in seconds (1-60).",
            DefaultValueFactory = _ => ProtocolConstants.DefaultWaitMilliseconds / 1000
        };
        AddRangeValidator(option, 1, ProtocolConstants.MaxWaitMilliseconds / 1000, "Timeout", "seconds");
        return option;
    }

    /// <summary>
    /// Creates an optional HTTP port with identical validation for both listener command groups.
    /// </summary>
    /// <param name="description">The command-specific help text, including any configured default.</param>
    public static Option<int?> CreatePortOption(string description)
    {
        var option = new Option<int?>("--port", "-p") { Description = description };
        AddRangeValidator(option, 1, 65535, "Port");
        return option;
    }

    public static void AddOptions(Command command, params Option[] options)
    {
        foreach (var option in options)
        {
            command.Options.Add(option);
        }
    }

    /// <summary>Validates integer bounds after a successful conversion, leaving missing and malformed values to the parser.</summary>
    /// <typeparam name="T">The integer or nullable integer option type.</typeparam>
    /// <param name="option">The numeric option to validate.</param>
    /// <param name="minimum">The inclusive lower bound.</param>
    /// <param name="maximum">The inclusive upper bound.</param>
    /// <param name="label">The name used in a range error.</param>
    /// <param name="unit">The optional unit displayed after the range.</param>
    public static void AddRangeValidator<T>(Option<T> option, int minimum, int maximum, string label, string? unit = null)
    {
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count != 1 || !int.TryParse(result.Tokens[0].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return;
            }
            if (value < minimum || value > maximum)
            {
                result.AddError($"{label} must be between {minimum} and {maximum}{(unit is null ? string.Empty : " " + unit)}.");
            }
        });
    }

    /// <summary>
    /// Writes the common replay and composition outcome, including optional result correlation.
    /// </summary>
    /// <param name="result">The queued request and any captured-session or timeout result.</param>
    public static void WriteQueuedResult(CliQueuedResult result)
    {
        Console.WriteLine(result.Queued.Message);
        if (result.CapturedSession != null)
        {
            Console.WriteLine($"Captured as session {result.CapturedSession.Id}: {result.CapturedSession.StatusCode} {result.CapturedSession.Url}");
        }
        else if (result.TimedOut)
        {
            Console.WriteLine("The request was accepted. Waiting for a matching completed session timed out.");
        }
    }
}
