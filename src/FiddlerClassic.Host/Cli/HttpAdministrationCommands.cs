// Builds persistent MCP HTTP service, client, and connection administration commands.
using System.CommandLine;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Protocol;
using static FiddlerClassic.Host.Cli.CommandHelpers;

namespace FiddlerClassic.Host.Cli;

internal static class HttpAdministrationCommands
{
    /// <summary>
    /// Creates listener status and configuration commands with the existing enable/disable confirmations.
    /// </summary>
    /// <param name="administration">The shared daemon-owned HTTP administration client.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command CreateService(
        HttpAdminClient administration,
        Option<bool> jsonOption)
    {
        var service = new Command("service", "Manage the persistent daemon-owned MCP HTTP listener.");
        var status = new Command("status", "Show saved and live MCP HTTP listener status.");
        status.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => administration.GetServiceStatusAsync(cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteHttpServiceStatus));

        var configure = new Command("configure", "Change the bind mode or port while the service is disabled.");
        var bind = new Option<string?>("--bind") { Description = "Bind mode: loopback or all." };
        bind.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string?>();
            if (value is not null
                && value != HttpBindModes.Loopback
                && value != HttpBindModes.All)
            {
                result.AddError("Bind mode must be 'loopback' or 'all'.");
            }
        });
        var port = CreatePortOption("TCP port from 1 through 65535.");
        configure.Options.Add(bind);
        configure.Options.Add(port);
        configure.Validators.Add(result =>
        {
            if (result.GetValue(bind) is null && !result.GetValue(port).HasValue)
            {
                result.AddError("Specify --bind, --port, or both.");
            }
        });
        configure.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => administration.ConfigureServiceAsync(
                parseResult.GetValue(bind),
                parseResult.GetValue(port),
                cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteHttpServiceStatus));

        var enable = new Command("enable", "Enable and start the persistent MCP HTTP listener.");
        var enableYes = new Option<bool>("--yes", "-y") { Description = "Acknowledge plaintext remote HTTP without prompting." };
        enable.Options.Add(enableYes);
        enable.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            try
            {
                var current = await administration.GetServiceStatusAsync(cancellationToken).ConfigureAwait(false);
                var confirmed = parseResult.GetValue(enableYes);
                if (current.BindMode == HttpBindModes.All && !confirmed)
                {
                    confirmed = CliOutput.Confirm(
                        "MCP HTTP on 0.0.0.0 sends bearer tokens without encryption. Continue?",
                        yes: false);
                    if (!confirmed)
                    {
                        return ConfirmationRequired(json);
                    }
                }

                var result = await administration.EnableServiceAsync(confirmed, cancellationToken).ConfigureAwait(false);
                CliOutput.Write(result, json, () => WriteHttpServiceStatus(result));
                return ExitCodes.Success;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return CliOutput.Error(exception, json);
            }
        });

        var disable = new Command("disable", "Disable the persistent MCP HTTP listener.");
        var disableYes = new Option<bool>("--yes", "-y") { Description = "Confirm active client disconnection without prompting." };
        disable.Options.Add(disableYes);
        disable.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            try
            {
                var current = await administration.GetServiceStatusAsync(cancellationToken).ConfigureAwait(false);
                var confirmed = parseResult.GetValue(disableYes);
                if (current.ActiveConnectionCount > 0 && !confirmed)
                {
                    confirmed = CliOutput.Confirm("Disconnect active MCP HTTP clients and disable the service?", yes: false);
                    if (!confirmed)
                    {
                        return ConfirmationRequired(json);
                    }
                }

                var result = await administration.DisableServiceAsync(confirmed, cancellationToken).ConfigureAwait(false);
                CliOutput.Write(result, json, () => WriteHttpServiceStatus(result));
                return ExitCodes.Success;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return CliOutput.Error(exception, json);
            }
        });

        service.Subcommands.Add(status);
        service.Subcommands.Add(configure);
        service.Subcommands.Add(enable);
        service.Subcommands.Add(disable);
        return service;
    }

    /// <summary>
    /// Creates client authorization and confirmed revocation commands without exposing tokens in listings.
    /// </summary>
    /// <param name="administration">The shared daemon-owned HTTP administration client.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command CreateClients(
        HttpAdminClient administration,
        Option<bool> jsonOption)
    {
        var clients = new Command("clients", "Authorize and deauthorize MCP HTTP clients.");
        var list = new Command("list", "List authorized clients without token material.");
        list.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => administration.ListClientsAsync(cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteHttpClients));

        var authorize = new Command("authorize", "Authorize a named client and print its bearer token once.");
        var name = new Option<string>("--name") { Description = "Unique client name, 1 to 64 characters.", Required = true };
        authorize.Options.Add(name);
        authorize.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => administration.AuthorizeClientAsync(parseResult.GetRequiredValue(name), cancellationToken),
            parseResult.GetValue(jsonOption),
            result =>
            {
                Console.WriteLine($"Authorized {result.Client.Name} ({result.Client.ClientId}).");
                Console.WriteLine(result.Token);
                Console.WriteLine("Copy this token now. It cannot be shown again.");
            }));

        var deauthorize = new Command("deauthorize", "Revoke a named client or rotate the default token.");
        var clientId = new Argument<string>("client-id") { Description = "Client ID returned by 'mcp clients list'." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm without prompting." };
        deauthorize.Arguments.Add(clientId);
        deauthorize.Options.Add(yes);
        deauthorize.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            if (!CliOutput.Confirm(
                    "Deauthorize this client? Active requests may already have completed.",
                    parseResult.GetValue(yes)))
            {
                return Task.FromResult(ConfirmationRequired(json));
            }

            return CliOutput.RunAsync(
                () => administration.DeauthorizeClientAsync(
                    parseResult.GetRequiredValue(clientId),
                    confirm: true,
                    cancellationToken),
                json,
                result => Console.WriteLine(
                    result.DefaultTokenRotated
                        ? "Rotated the default CLI token and disconnected its active connections."
                        : $"Deauthorized {result.ClientId} and disconnected {result.DisconnectedConnectionCount} connection(s)."));
        });

        clients.Subcommands.Add(list);
        clients.Subcommands.Add(authorize);
        clients.Subcommands.Add(deauthorize);
        return clients;
    }

    /// <summary>
    /// Creates connection inspection and confirmed disconnection commands.
    /// </summary>
    /// <param name="administration">The shared daemon-owned HTTP administration client.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    public static Command CreateConnections(
        HttpAdminClient administration,
        Option<bool> jsonOption)
    {
        var connections = new Command("connections", "Inspect or disconnect active MCP HTTP transports.");
        var list = new Command("list", "List active MCP HTTP connections.");
        list.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(
            () => administration.ListConnectionsAsync(cancellationToken),
            parseResult.GetValue(jsonOption),
            WriteHttpConnections));

        var disconnect = new Command("disconnect", "Abort one active MCP HTTP connection.");
        var connectionId = new Argument<string>("connection-id") { Description = "Connection ID returned by 'mcp connections list'." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm without prompting." };
        disconnect.Arguments.Add(connectionId);
        disconnect.Options.Add(yes);
        disconnect.SetAction((parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            if (!CliOutput.Confirm(
                    "Disconnect this MCP HTTP connection? An active operation may already have completed.",
                    parseResult.GetValue(yes)))
            {
                return Task.FromResult(ConfirmationRequired(json));
            }

            return CliOutput.RunAsync(
                () => administration.DisconnectAsync(
                    parseResult.GetRequiredValue(connectionId),
                    confirm: true,
                    cancellationToken),
                json,
                result => Console.WriteLine($"Disconnected {result.ConnectionId}."));
        });

        connections.Subcommands.Add(list);
        connections.Subcommands.Add(disconnect);
        return connections;
    }

    private static void WriteHttpServiceStatus(HttpServiceStatus status)
    {
        Console.WriteLine($"MCP HTTP enabled:     {status.Enabled}");
        Console.WriteLine($"MCP HTTP running:     {status.Running}");
        Console.WriteLine($"Bind:                 {status.BindAddress}:{status.Port} ({status.BindMode})");
        Console.WriteLine($"Loopback endpoint:    http://127.0.0.1:{status.Port}/mcp");
        if (status.BindMode == HttpBindModes.All)
        {
            foreach (var endpoint in (status.LanEndpoints ?? Array.Empty<string>()).Take(8))
                Console.WriteLine($"LAN endpoint hint:    {endpoint}");
            Console.WriteLine("LAN hints are local adapter addresses; remote reachability has not been tested.");
        }
        Console.WriteLine($"Active connections:   {status.ActiveConnectionCount}");
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            Console.WriteLine($"Last error:           {status.LastError}");
        }
    }

    private static void WriteHttpClients(ListHttpClientsResponse response)
    {
        Console.WriteLine($"Authorized clients: {response.Clients.Count}");
        Console.WriteLine($"{"ID",32} {"ACTIVE",6} {"LAST SEEN",27} NAME");
        foreach (var client in response.Clients)
        {
            Console.WriteLine(
                $"{client.ClientId,32} {client.ActiveConnectionCount,6} {client.LastSeenAtUtc ?? "-",27} {client.Name}");
        }
    }

    private static void WriteHttpConnections(ListHttpConnectionsResponse response)
    {
        Console.WriteLine($"Active connections: {response.Connections.Count}");
        Console.WriteLine($"{"STATE",10} {"ACTIVE",6} {"REQUESTS",8} {"REMOTE",24} ID");
        foreach (var connection in response.Connections)
        {
            Console.WriteLine(
                $"{connection.AuthorizationState,10} {connection.ActiveRequestCount,6} {connection.TotalRequestCount,8} {connection.RemoteEndpoint,24} {connection.ConnectionId}");
            if (connection.ClientNames.Length > 0)
            {
                Console.WriteLine($"  clients: {string.Join(", ", connection.ClientNames)}");
            }
        }
    }
}
