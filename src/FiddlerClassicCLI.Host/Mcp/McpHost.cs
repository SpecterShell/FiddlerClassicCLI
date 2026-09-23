// Configures MCP stdio and reusable HTTP transports with explicit access policies.
using System.Net;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace FiddlerClassicCLI.Host.Mcp;

internal static class McpHost
{
    public static async Task RunStdioAsync(CancellationToken cancellationToken)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        ConfigureLogging(builder.Logging);
        RegisterServices(builder.Services);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<FiddlerTools>()
            .WithTools<SessionSummaryTools>()
            .WithTools<AutoResponderTools>()
            .WithTools<BreakpointTools>();

        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task RunHttpAsync(int port, ConfigStore configStore, CancellationToken cancellationToken)
    {
        await using var server = await StartHttpAsync(
            IPAddress.Loopback,
            port,
            new HttpCredentialManager(configStore),
            new HttpConnectionRegistry(),
            cancellationToken).ConfigureAwait(false);
        Console.Error.WriteLine($"Fiddler Classic MCP listening on http://127.0.0.1:{port}/mcp");
        await server.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static Task<ManagedHttpServer> StartHttpAsync(
        IPAddress address,
        int port,
        IHttpCredentialProvider credentialProvider,
        HttpConnectionRegistry connections,
        CancellationToken cancellationToken)
    {
        return StartHttpAsync([address], port, credentialProvider, connections, cancellationToken);
    }

    /// <summary>Starts one HTTP service across all bindings, disposing every listener if startup fails.</summary>
    /// <param name="addresses">Local addresses to bind on the same port.</param>
    /// <param name="port">The validated TCP port shared by every endpoint.</param>
    /// <param name="credentialProvider">Credentials used only when authentication is required.</param>
    /// <param name="connections">Tracks native transports across all endpoints.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <param name="authenticationMode">Selects required, non-loopback, or anonymous access.</param>
    /// <returns>The running service, owned by the caller.</returns>
    internal static async Task<ManagedHttpServer> StartHttpAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        IHttpCredentialProvider credentialProvider,
        HttpConnectionRegistry connections,
        CancellationToken cancellationToken,
        string authenticationMode = HttpAuthenticationModes.Required)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        ArgumentNullException.ThrowIfNull(connections);
        if (!HttpAuthenticationModes.IsValid(authenticationMode))
            throw new ArgumentException("Unsupported HTTP authentication mode.", nameof(authenticationMode));
        var bindings = addresses.ToArray();
        if (bindings.Length == 0 || bindings.Any(address => address is null))
        {
            throw new ArgumentException("At least one local IP address is required.", nameof(addresses));
        }
        ConfigStore.ValidatePort(port);
        var builder = WebApplication.CreateBuilder();
        ConfigureLogging(builder.Logging);
        // Framework request diagnostics may include URLs, headers, or tool arguments.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);
        builder.WebHost.ConfigureKestrel(options =>
        {
            foreach (var address in bindings)
            {
                options.Listen(address, port, listenOptions =>
                {
                    listenOptions.Use(next => context => connections.TrackAsync(context, next));
                });
            }
        });
        RegisterServices(builder.Services);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<FiddlerTools>()
            .WithTools<SessionSummaryTools>()
            .WithTools<AutoResponderTools>()
            .WithTools<BreakpointTools>();

        var app = builder.Build();
        app.Use((context, next) => HttpAccessPolicy.InvokeAsync(
            context, next, authenticationMode, credentialProvider, connections));
        app.MapMcp("/mcp");

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            return new ManagedHttpServer(app, connections);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static bool IsValidBearerValue(string? authorization, string token)
    {
        return new SingleTokenCredentialProvider(token).Authenticate(authorization) is not null;
    }

    private static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IBridgeClient, NamedPipeBridgeClient>();
        services.AddSingleton<FiddlerEnvironment>();
        services.AddSingleton<StatusService>();
    }

    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    }
}

internal sealed class ManagedHttpServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly HttpConnectionRegistry _connections;
    private int _stopped;

    public ManagedHttpServer(WebApplication application, HttpConnectionRegistry connections)
    {
        _application = application;
        _connections = connections;
    }

    public async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _application.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _connections.DisconnectAll();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _application.StopAsync(timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
