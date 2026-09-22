// Formats CLI results, confirmations, and stable process errors.
using System.Text.Json;
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Host.Services;

namespace FiddlerClassic.Host.Cli;

internal static class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Writes JSON by request or delegates to a human-readable writer when one is available.
    /// </summary>
    /// <param name="value">The result serialized for JSON output.</param>
    /// <param name="json">Whether to emit structured JSON.</param>
    /// <param name="humanWriter">The optional human-readable writer.</param>
    public static void Write(object value, bool json, Action? humanWriter = null)
    {
        if (json || humanWriter is null)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
            return;
        }

        humanWriter();
    }

    /// <summary>
    /// Writes a stable error representation to stderr and returns its documented process exit code.
    /// </summary>
    /// <param name="exception">The bridge, daemon, or unexpected failure to report.</param>
    /// <param name="json">Whether stderr should contain structured JSON.</param>
    public static int Error(Exception exception, bool json)
    {
        var code = exception switch
        {
            BridgeClientException bridgeException => bridgeException.Code,
            DaemonClientException daemonException => daemonException.Code,
            HttpAdministrationException administrationException => administrationException.Code,
            _ => "internal_error"
        };
        var exitCode = exception is BridgeClientException or DaemonClientException or HttpAdministrationException
            ? ExitCodes.ForBridgeError(code)
            : ExitCodes.Failure;

        if (json)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = new { code, message = exception.Message } }, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine($"Error [{code}]: {exception.Message}");
        }

        return exitCode;
    }

    /// <summary>
    /// Executes a command action and converts expected failures into process exit codes.
    /// </summary>
    /// <param name="action">The asynchronous command operation.</param>
    /// <param name="json">Whether failures should be written as JSON.</param>
    public static async Task<int> RunAsync(Func<Task> action, bool json)
    {
        try
        {
            await action().ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(exception, json);
        }
    }

    /// <summary>
    /// Executes a value-returning command, writes its selected representation, and maps failures to exit codes.
    /// </summary>
    /// <typeparam name="T">The command result type.</typeparam>
    /// <param name="action">The asynchronous command operation.</param>
    /// <param name="json">Whether the result and failures should use JSON.</param>
    /// <param name="humanWriter">An optional human-readable result writer.</param>
    public static async Task<int> RunAsync<T>(Func<Task<T>> action, bool json, Action<T>? humanWriter = null)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            Write(result!, json, humanWriter is null ? null : () => humanWriter(result));
            return ExitCodes.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(exception, json);
        }
    }

    /// <summary>
    /// Honors an explicit confirmation flag or prompts only when stdin is interactive.
    /// </summary>
    /// <param name="prompt">The confirmation question written to stderr.</param>
    /// <param name="yes">Whether the caller already supplied explicit confirmation.</param>
    public static bool Confirm(string prompt, bool yes)
    {
        if (yes)
        {
            return true;
        }

        if (Console.IsInputRedirected)
        {
            return false;
        }

        Console.Error.Write($"{prompt} [y/N] ");
        var answer = Console.ReadLine();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
