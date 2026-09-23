// Builds persistent MCP HTTP service, client, and connection administration commands.
using System.CommandLine;
using System.Net;
using FiddlerClassicCLI.Host.Daemon;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;
using static FiddlerClassicCLI.Host.Cli.CommandHelpers;

namespace FiddlerClassicCLI.Host.Cli;

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

        var enable = new Command("enable", "Enable and start the persistent MCP HTTP listener.");
        var enableYes = new Option<bool>("--yes", "-y")
        {
            Description = "Acknowledge remote plaintext HTTP or unauthenticated access without prompting."
        };
        enable.Options.Add(enableYes);
        enable.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            try
            {
                var current = await administration.GetServiceStatusAsync(cancellationToken).ConfigureAwait(false);
                var confirmed = parseResult.GetValue(enableYes);
                if (current.AuthenticationMode == HttpAuthenticationModes.None
                    || IsRemoteBinding(current.BindMode, current.BindAddresses))
                {
                    confirmed = ConfirmListenerRisk(current.AuthenticationMode, atStartup: false, confirmed);
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
        service.Subcommands.Add(CreateConfigure(administration, jsonOption));
        service.Subcommands.Add(enable);
        service.Subcommands.Add(disable);
        return service;
    }

    /// <summary>Builds explicit listener configuration with parser validation and security confirmation.</summary>
    /// <param name="administration">The daemon-aware configuration client.</param>
    /// <param name="jsonOption">The inherited machine-readable output option.</param>
    private static Command CreateConfigure(HttpAdminClient administration, Option<bool> jsonOption)
    {
        var configure = new Command("configure",
            "Configure MCP HTTP. Disable the service before changing bind addresses, port, or authentication.");
        var bind = new Option<string?>("--bind") { Description = "Bind mode: loopback, all, or selected IPv4 addresses." };
        bind.AcceptOnlyFromAmong(HttpBindModes.Loopback, HttpBindModes.All, HttpBindModes.Selected);
        var addresses = new Option<string[]>("--address")
        {
            Description = $"Local IPv4 address to listen on (up to {HttpListenerLimits.MaximumSelectedAddresses}). Repeat as needed. Use --bind selected or saved selected mode.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        addresses.Validators.Add(result =>
        {
            if (result.Tokens.Count > HttpListenerLimits.MaximumSelectedAddresses)
                result.AddError($"Specify at most {HttpListenerLimits.MaximumSelectedAddresses} --address values.");
            foreach (var token in result.Tokens)
            {
                if (!HttpListenerSettings.IsUnicastAddress(token.Value))
                {
                    result.AddError("Each --address must be an IPv4 unicast address in dotted-decimal form.");
                }
            }
        });
        var port = CreatePortOption("TCP port from 1 through 65535.");
        var startup = new Option<string?>("--startup")
        {
            Description = "Startup policy: enabled, disabled, or last-state. Applies when the daemon starts or Fiddler opens."
        };
        startup.AcceptOnlyFromAmong(HttpStartupModes.Enabled, HttpStartupModes.Disabled, HttpStartupModes.LastState);
        var authentication = new Option<string?>("--authentication")
        {
            Description = "Authentication: required (bearer token for all clients), non-loopback (bearer token for non-loopback clients), or none (requires confirmation)."
        };
        authentication.AcceptOnlyFromAmong(
            HttpAuthenticationModes.Required, HttpAuthenticationModes.NonLoopback, HttpAuthenticationModes.None);
        foreach (var option in new[] { bind, startup, authentication })
        {
            // Required string options can consume the next flag as a value before enum validation.
            option.Validators.Add(result =>
            {
                if (!result.Implicit && (result.Tokens.Count == 0
                    || result.Tokens.Any(token => token.Value.StartsWith('-'))))
                    result.AddError($"Option '{option.Name}' requires a value.");
            });
        }
        var yes = new Option<bool>("--yes", "-y")
        {
            Description = "Acknowledge unauthenticated access or automatic remote startup without prompting."
        };
        AddOptions(configure, bind, addresses, port, startup, authentication, yes);
        configure.Validators.Add(result =>
        {
            if (!new Option[] { bind, addresses, port, startup, authentication }
                .Any(option => result.GetResult(option) is { Implicit: false }))
            {
                result.AddError("Specify --bind, --address, --port, --startup, or --authentication.");
            }
            // Validate relationships from tokens so failed enum conversions remain parser errors.
            var hasAddresses = result.GetResult(addresses)?.Tokens.Count > 0;
            var mode = result.GetResult(bind)?.Tokens.FirstOrDefault()?.Value;
            if (hasAddresses && mode is not null && mode != HttpBindModes.Selected)
                result.AddError("--address requires selected bind mode.");
            if (mode == HttpBindModes.Selected && !hasAddresses)
                result.AddError("--bind selected requires at least one --address.");
        });
        configure.SetAction((parseResult, cancellationToken) => CliOutput.RunAsync(async () =>
        {
            var request = new ConfigureHttpServiceRequest
            {
                BindMode = parseResult.GetValue(bind),
                BindAddresses = parseResult.GetResult(addresses) is { Implicit: false }
                    ? parseResult.GetValue(addresses) : null,
                Port = parseResult.GetValue(port),
                StartupMode = parseResult.GetValue(startup),
                AuthenticationMode = parseResult.GetValue(authentication),
                Confirm = parseResult.GetValue(yes)
            };
            var current = await administration.GetServiceStatusAsync(cancellationToken).ConfigureAwait(false);
            var authenticationMode = request.AuthenticationMode ?? current.AuthenticationMode;
            var startsEnabled = (request.StartupMode ?? current.StartupMode) == HttpStartupModes.Enabled;
            if (request.AuthenticationMode == HttpAuthenticationModes.None
                || (startsEnabled && (authenticationMode == HttpAuthenticationModes.None
                    || IsRemoteBinding(request.BindMode ?? current.BindMode, request.BindAddresses ?? current.BindAddresses))))
            {
                request.Confirm = ConfirmListenerRisk(authenticationMode, startsEnabled, request.Confirm);
            }
            return await administration.ConfigureServiceAsync(request, cancellationToken).ConfigureAwait(false);
        }, parseResult.GetValue(jsonOption), WriteHttpServiceStatus));
        return configure;
    }

    private static bool IsRemoteBinding(string mode, string[] addresses) => mode == HttpBindModes.All
        || (mode == HttpBindModes.Selected && addresses.Any(address =>
            !IPAddress.TryParse(address, out var parsed) || !IPAddress.IsLoopback(parsed)));

    private static bool ConfirmListenerRisk(string authenticationMode, bool atStartup, bool yes)
    {
        var warning = authenticationMode == HttpAuthenticationModes.None
            ? "Anyone who can reach MCP HTTP can control Fiddler without a token when authentication is disabled."
            : "Remote MCP HTTP sends bearer credentials without encryption. Anyone who observes a credential can reuse it.";
        if (atStartup) warning += " MCP HTTP will be enabled automatically at startup.";
        if (!CliOutput.Confirm(warning + " Continue?", yes))
        {
            throw new HttpAdministrationException(ErrorCodes.ConfirmationRequired,
                warning + " Use --yes to confirm in non-interactive use.");
        }
        return true;
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
        Console.WriteLine($"Startup policy:       {status.StartupMode}");
        var authentication = status.AuthenticationMode switch
        {
            HttpAuthenticationModes.Required => "required (bearer token for all clients)",
            HttpAuthenticationModes.NonLoopback => "non-loopback (bearer token required outside loopback)",
            HttpAuthenticationModes.None => "none (unauthenticated access)",
            _ => status.AuthenticationMode
        };
        Console.WriteLine($"Authentication:       {authentication}");
        Console.WriteLine($"Bind:                 {status.BindAddress}:{status.Port} ({status.BindMode})");
        foreach (var endpoint in status.Endpoints)
            Console.WriteLine($"Listener endpoint:    {endpoint}");
        if (!string.IsNullOrEmpty(status.LoopbackEndpoint))
            Console.WriteLine($"Loopback endpoint:    {status.LoopbackEndpoint}");
        if (status.BindMode == HttpBindModes.All)
        {
            foreach (var endpoint in (status.LanEndpoints ?? Array.Empty<string>()).Take(8))
                Console.WriteLine($"LAN endpoint hint:    {endpoint}");
            Console.WriteLine("LAN hints are local adapter addresses. Check remote reachability from the client.");
        }
        foreach (var address in status.AvailableInterfaces)
            Console.WriteLine($"Available interface:  {address.Address} ({address.AdapterName})");
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
