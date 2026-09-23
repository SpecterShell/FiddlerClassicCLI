// Configures MCP stdio and reusable authenticated HTTP transports.
using System.Net;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Services;
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

    internal static async Task<ManagedHttpServer> StartHttpAsync(
        IPAddress address,
        int port,
        IHttpCredentialProvider credentialProvider,
        HttpConnectionRegistry connections,
        CancellationToken cancellationToken)
    {
        ConfigStore.ValidatePort(port);
        var builder = WebApplication.CreateBuilder();
        ConfigureLogging(builder.Logging);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, port, listenOptions =>
            {
                listenOptions.Use(next => context => connections.TrackAsync(context, next));
            });
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
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/mcp"))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var identity = credentialProvider.Authenticate(context.Request.Headers.Authorization.ToString());
            connections.BeginRequest(context.Connection.Id, identity);
            try
            {
                // No browser origins are authorized. Reject even opaque or malformed origins.
                // Absent CORS response headers alone do not prevent browser-initiated requests.
                if (context.Request.Headers.ContainsKey("Origin"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                if (identity is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }

                await next(context).ConfigureAwait(false);
            }
            finally
            {
                connections.EndRequest(context.Connection.Id);
            }
        });
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
        await StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
