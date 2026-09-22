// Shares CLI option validation, confirmation errors, and queued-request output.
using System.CommandLine;
using System.Globalization;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Cli;

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
        option.Validators.Add(result =>
        {
            // Let the parser report missing or nonnumeric values without throwing from a validator.
            if (result.Tokens.Count != 1 || !int.TryParse(result.Tokens[0].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return;
            }
            if (value < 1 || value > ProtocolConstants.MaxWaitMilliseconds / 1000)
            {
                result.AddError("Timeout must be between 1 and 60 seconds.");
            }
        });
        return option;
    }

    /// <summary>
    /// Creates an optional HTTP port with identical validation for both listener command groups.
    /// </summary>
    /// <param name="description">The command-specific help text, including any configured default.</param>
    public static Option<int?> CreatePortOption(string description)
    {
        var option = new Option<int?>("--port", "-p") { Description = description };
        option.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int?>();
            if (value.HasValue && value.Value is < 1 or > 65535)
            {
                result.AddError("Port must be between 1 and 65535.");
            }
        });
        return option;
    }

    public static void AddOptions(Command command, params Option[] options)
    {
        foreach (var option in options)
        {
            command.Options.Add(option);
        }
    }

    public static void AddRangeValidator(Option<int> option, int minimum, int maximum, string label)
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
            Console.WriteLine("The request was accepted, but no matching completed session arrived before timeout.");
        }
    }
}
