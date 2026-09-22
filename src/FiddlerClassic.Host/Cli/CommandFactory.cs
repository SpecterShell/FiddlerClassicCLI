// Composes the CLI command tree and binds host diagnostics and lifecycle commands.
using System.CommandLine;
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Host.Services;
using FiddlerClassic.Protocol;
using static FiddlerClassic.Host.Cli.CommandHelpers;

namespace FiddlerClassic.Host.Cli;

internal static class CommandFactory
{
    /// <summary>
    /// Builds the root command, shared JSON option, and all supported command groups.
    /// </summary>
    /// <param name="actions">The application operations bound to command handlers.</param>
    /// <param name="daemonClient">The daemon lifecycle client bound to daemon commands.</param>
    /// <param name="appService">Optional Fiddler application lifecycle service, replaced by fake processes in tests.</param>
    public static RootCommand Create(CliActions actions, DaemonClient daemonClient, FiddlerAppService? appService = null)
    {
        var root = new RootCommand("Unofficial CLI and MCP server for Fiddler Classic 5.x and 6.x on Windows.");
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write machine-readable JSON.",
            Recursive = true
        };
        root.Options.Add(jsonOption);

        root.Subcommands.Add(CreateDoctor(actions, daemonClient, jsonOption));
        root.Subcommands.Add(CreateStatus(actions, jsonOption));
        root.Subcommands.Add(FiddlerAppCommands.Create(appService ?? new FiddlerAppService(), jsonOption));
        root.Subcommands.Add(CreateCapture(actions, jsonOption));
        root.Subcommands.Add(SessionCommands.Create(actions, jsonOption));
        root.Subcommands.Add(RequestCommands.Create(actions, jsonOption));
        root.Subcommands.Add(AutoResponderCommands.Create(actions, jsonOption));
        root.Subcommands.Add(BreakpointCommands.Create(actions, jsonOption));
        root.Subcommands.Add(CreateBridge(actions, jsonOption));
        root.Subcommands.Add(CreateDaemon(daemonClient, jsonOption));
        root.Subcommands.Add(McpCommands.Create(actions, daemonClient, jsonOption));
        root.Subcommands.Add(CreateConfig(actions, jsonOption));
        return root;
    }

    /// <summary>
    /// Creates the diagnostic command and maps an unhealthy result to the appropriate exit code.
    /// </summary>
    /// <param name="actions">The CLI operations used by the handler.</param>
    /// <param name="daemonClient">Probes daemon status without starting it during export.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateDoctor(CliActions actions, DaemonClient daemonClient, Option<bool> jsonOption)
    {
        var command = new Command("doctor", "Check Fiddler, bridge, process, pipe, and configuration health.");
        var output = new Option<string?>("--output")
        {
            Arity = ArgumentArity.ExactlyOne,
            Description = "Write metadata-only diagnostics to a new absolute file without starting the daemon."
        };
        command.Options.Add(output);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            try
            {
                if (parseResult.GetValue(output) is string path)
                {
                    try
                    {
                        var receipt = await actions.ExportDiagnostics(path, daemonClient, cancellationToken).ConfigureAwait(false);
                        CliOutput.Write(receipt, json, () => Console.WriteLine($"Diagnostic report: {receipt.Path}"));
                        return ExitCodes.Success;
                    }
                    catch (ArgumentException exception)
                    {
                        return CliOutput.Error(new BridgeClientException(ErrorCodes.InvalidRequest, exception.Message), json);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        return CliOutput.Error(new BridgeClientException(ErrorCodes.Conflict,
                            "Could not create the diagnostic file. Choose a new file in an existing writable directory."), json);
                    }
                }
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
}
