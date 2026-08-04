// Configures MCP stdio and authenticated loopback HTTP transports.
using System.Security.Cryptography;
using System.Text;
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Host.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FiddlerClassic.Host.Mcp;

internal static class McpHost
{
    /// <summary>
    /// Runs the MCP server over stdio while routing every diagnostic log to stderr.
    /// </summary>
    /// <param name="cancellationToken">Stops the host and stdio transport.</param>
    public static async Task RunStdioAsync(CancellationToken cancellationToken)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        ConfigureLogging(builder.Logging);
        RegisterServices(builder.Services);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<FiddlerTools>()
            .WithTools<AutoResponderTools>()
            .WithTools<BreakpointTools>();

        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs stateless MCP over bearer-authenticated Streamable HTTP bound only to IPv4 loopback.
    /// </summary>
    /// <param name="port">The loopback TCP port.</param>
    /// <param name="token">The required bearer token.</param>
    /// <param name="cancellationToken">Stops the web host.</param>
    public static async Task RunHttpAsync(int port, string token, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        var builder = WebApplication.CreateBuilder();
        ConfigureLogging(builder.Logging);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        RegisterServices(builder.Services);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<FiddlerTools>()
            .WithTools<AutoResponderTools>()
            .WithTools<BreakpointTools>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp") && !HasValidBearerToken(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        app.MapMcp("/mcp");

        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a bearer authorization value and compares its token in fixed time.
    /// </summary>
    /// <param name="authorization">The complete Authorization header value.</param>
    /// <param name="token">The expected token.</param>
    internal static bool IsValidBearerValue(string? authorization, string token)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var supplied = Encoding.UTF8.GetBytes(authorization[prefix.Length..]);
        var expected = Encoding.UTF8.GetBytes(token);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static bool HasValidBearerToken(HttpContext context, string token)
    {
        return IsValidBearerValue(context.Request.Headers.Authorization.ToString(), token);
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
