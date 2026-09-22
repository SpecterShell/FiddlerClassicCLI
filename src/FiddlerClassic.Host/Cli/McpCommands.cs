// Builds MCP transport commands and composes persistent HTTP administration.
using System.CommandLine;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Host.Mcp;
using FiddlerClassic.Host.Services;
using FiddlerClassic.Protocol;
using static FiddlerClassic.Host.Cli.CommandHelpers;

namespace FiddlerClassic.Host.Cli;

internal static class McpCommands
{
    /// <summary>
    /// Creates MCP transports and persistent HTTP administration commands.
    /// </summary>
    /// <param name="actions">Provides the persisted HTTP configuration.</param>
    /// <param name="daemonClient">Controls the daemon-owned HTTP service.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command Create(
        CliActions actions,
        DaemonClient daemonClient,
        Option<bool> jsonOption)
    {
        var mcp = new Command("mcp", "Run the Fiddler Classic MCP server.");
        var administration = new HttpAdminClient(daemonClient, actions.ConfigurationStore);
        var stdio = new Command("stdio", "Run MCP over standard input/output.");
        stdio.SetAction(async (_, cancellationToken) =>
        {
            await McpHost.RunStdioAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        });

        var http = new Command("http", "Run stateless Streamable HTTP MCP on loopback.");
        var port = CreatePortOption("Loopback TCP port; defaults to the configured port (8877).");
        http.Options.Add(port);
        http.SetAction(async (parseResult, cancellationToken) =>
        {
            var configuration = actions.GetConfiguration();
            var selectedPort = parseResult.GetValue(port) ?? configuration.HttpPort;
            try
            {
                await McpHost.RunHttpAsync(selectedPort, actions.ConfigurationStore, cancellationToken).ConfigureAwait(false);
                return ExitCodes.Success;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                return CliOutput.Error(
                    new HttpAdministrationException(
                        ErrorCodes.Unavailable,
                        $"MCP HTTP could not bind to 127.0.0.1:{selectedPort}. Another listener, including the managed MCP HTTP service, may already own the port. {exception.Message}",
                        exception),
                    parseResult.GetValue(jsonOption));
            }
        });

        mcp.Subcommands.Add(HttpAdministrationCommands.CreateService(administration, jsonOption));
        mcp.Subcommands.Add(HttpAdministrationCommands.CreateClients(administration, jsonOption));
        mcp.Subcommands.Add(HttpAdministrationCommands.CreateConnections(administration, jsonOption));

        mcp.Subcommands.Add(stdio);
        mcp.Subcommands.Add(http);
        return mcp;
    }
}
