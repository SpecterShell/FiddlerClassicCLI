// Binds explicit Fiddler application discovery and lifecycle commands, separate from daemon and capture controls.
using System.CommandLine;
using System.Globalization;
using FiddlerClassic.Host.Services;
using static FiddlerClassic.Host.Cli.CommandHelpers;

namespace FiddlerClassic.Host.Cli;

internal static class FiddlerAppCommands
{
    /// <summary>Builds commands that never use the bridge or auto-start the daemon.</summary>
    /// <param name="service">Installation discovery and confirmed native lifecycle operations.</param>
    /// <param name="json">The inherited machine-readable output switch.</param>
    public static Command Create(FiddlerAppService service, Option<bool> json)
    {
        var app = new Command("app", "Detect, open, close, or restart Fiddler Classic itself.");
        var detect = new Command("detect", "List installed Fiddler executables and current-user processes without starting anything.");
        var detectPath = CreatePathOption();
        detect.Options.Add(detectPath);
        detect.SetAction(parse =>
        {
            try
            {
                var result = service.Detect(parse.GetValue(detectPath));
                CliOutput.Write(result, parse.GetValue(json), () => WriteDetection(result));
                return result.Installations.Count > 0 || result.Processes.Count > 0 ? ExitCodes.Success : ExitCodes.NotInstalled;
            }
            catch (Exception exception)
            {
                return CliOutput.Error(exception, parse.GetValue(json));
            }
        });
        app.Subcommands.Add(detect);

        var open = new Command("open", "Open Fiddler with -noattach, or report the existing process. Does not wait for bridge readiness.");
        var openPath = CreatePathOption();
        open.Options.Add(openPath);
        open.SetAction((parse, cancellationToken) => CliOutput.RunAsync(
            () => Task.FromResult(service.Open(parse.GetValue(openPath), cancellationToken)),
            parse.GetValue(json), WriteResult));
        app.Subcommands.Add(open);
        app.Subcommands.Add(CreateClose(service, json, restart: false));
        app.Subcommands.Add(CreateClose(service, json, restart: true));
        return app;
    }

    /// <summary>Creates a confirmed normal-close operation; restart uses the selected executable after exit.</summary>
    /// <param name="service">The lifecycle coordinator.</param>
    /// <param name="json">The inherited machine-readable output switch.</param>
    /// <param name="restart">Whether to relaunch after the close completes.</param>
    private static Command CreateClose(FiddlerAppService service, Option<bool> json, bool restart)
    {
        var command = new Command(restart ? "restart" : "close", restart
            ? "Close Fiddler normally, then reopen with -noattach. Save captures first."
            : "Close Fiddler normally. Save captures first; never force-kills the process.");
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm possible loss of unsaved captures; does not dismiss Fiddler dialogs." };
        var pid = new Option<int?>("--pid") { Description = "Select one Fiddler PID owned by this user in this Windows session." };
        pid.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && int.TryParse(result.Tokens[0].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value <= 0)
            {
                result.AddError("PID must be a positive integer.");
            }
        });
        var timeout = CreateTimeoutOption();
        timeout.Description = "Wait for normal exit in seconds (1-60; default 10).";
        AddOptions(command, pid, timeout, yes);
        var path = CreatePathOption();
        if (restart)
        {
            command.Options.Add(path);
            command.Validators.Add(result =>
            {
                if (result.GetResult(path) is { Implicit: false } && result.GetResult(pid) is { Implicit: false })
                {
                    result.AddError("Use either --path or --pid, not both.");
                }
            });
        }
        command.SetAction((parse, cancellationToken) =>
        {
            var asJson = parse.GetValue(json);
            if (!CliOutput.Confirm("Close Fiddler? Unsaved captures may be lost and active capture will stop. Save needed evidence first.", parse.GetValue(yes)))
            {
                return Task.FromResult(ConfirmationRequired(asJson));
            }
            return CliOutput.RunAsync(() => service.CloseAsync(restart, parse.GetValue(pid),
                restart ? parse.GetValue(path) : null, parse.GetValue(timeout), true, cancellationToken), asJson, WriteResult);
        });
        return command;
    }

    private static Option<string?> CreatePathOption()
    {
        var option = new Option<string?>("--path")
        {
            Description = "Absolute path to an installed Fiddler.exe; overrides automatic installation discovery.",
            Arity = ArgumentArity.ExactlyOne
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count != 1) return;
            try
            {
                // Validate syntax before confirmation, including a flag consumed as the missing value.
                FiddlerEnvironment.NormalizeExecutablePath(result.Tokens[0].Value);
            }
            catch (ArgumentException exception)
            {
                result.AddError(exception.Message);
            }
        });
        return option;
    }

    private static void WriteDetection(FiddlerAppDetection result)
    {
        if (result.Installations.Count == 0)
        {
            Console.WriteLine("No Fiddler installation found in known locations. Use --path for a custom installation.");
        }
        foreach (var installation in result.Installations)
        {
            Console.WriteLine($"Installed: {installation.ExecutablePath} ({installation.Version ?? "unknown version"}; {(installation.Supported ? "supported" : "unsupported")})");
        }
        if (result.Processes.Count == 0)
        {
            Console.WriteLine("Fiddler is not running for this user in this Windows session.");
        }
        foreach (var process in result.Processes)
        {
            Console.WriteLine($"Running: PID {process.ProcessId} {process.ExecutablePath} ({process.Version ?? "unknown version"})");
        }
    }

    private static void WriteResult(FiddlerAppResult result)
    {
        Console.WriteLine(result.Running
            ? $"Fiddler {(result.Changed ? "launch requested with -noattach" : "already running")}: PID {result.ProcessId} {result.ExecutablePath}"
            : result.Changed ? "Fiddler closed normally." : "Fiddler was already closed.");
        if (result.Running && result.Changed)
        {
            Console.WriteLine("Check status or doctor after Fiddler loads; launch does not establish bridge readiness.");
        }
    }
}
