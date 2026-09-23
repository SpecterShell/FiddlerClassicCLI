// Enforces bearer policy from socket addresses and rejects browser-origin and DNS-rebinding requests.
using System.Net;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Mcp;

internal static class HttpAccessPolicy
{
    /// <summary>Applies the selected policy before dispatching any MCP request.</summary>
    /// <param name="context">The request and Kestrel connection metadata, without forwarded-header rewriting.</param>
    /// <param name="next">The MCP endpoint, invoked only after access checks pass.</param>
    /// <param name="authenticationMode">The validated listener policy.</param>
    /// <param name="credentialProvider">Checks credentials only for requests that require them.</param>
    /// <param name="connections">Records operational metadata without headers or token material.</param>
    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next, string authenticationMode,
        IHttpCredentialProvider credentialProvider, HttpConnectionRegistry connections)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var requireAuthentication = RequiresAuthentication(authenticationMode,
            context.Connection.LocalIpAddress, context.Connection.RemoteIpAddress);
        var identity = requireAuthentication
            ? credentialProvider.Authenticate(context.Request.Headers.Authorization.ToString())
            : null;
        var hasOrigin = context.Request.Headers.ContainsKey("Origin");
        var anonymous = !requireAuthentication && !hasOrigin && IsAllowedAnonymousHost(
            context.Request.Host, context.Connection.LocalIpAddress, context.Connection.LocalPort);
        connections.BeginRequest(context.Connection.Id, identity, anonymous);
        try
        {
            // No browser origins are authorized. Reject even opaque or malformed origins.
            // Absent CORS response headers alone do not prevent browser-initiated requests.
            if (hasOrigin || (!requireAuthentication && !anonymous))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (requireAuthentication && identity is null)
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
    }

    /// <summary>Exempts only connections whose local and remote endpoints are both loopback.</summary>
    /// <param name="mode">The listener authentication policy.</param>
    /// <param name="localAddress">The actual receiving socket address.</param>
    /// <param name="remoteAddress">The actual peer address. Unknown peers always require authentication.</param>
    internal static bool RequiresAuthentication(string mode, IPAddress? localAddress, IPAddress? remoteAddress) =>
        mode switch
        {
            HttpAuthenticationModes.None => false,
            HttpAuthenticationModes.NonLoopback => !IsLoopback(localAddress) || !IsLoopback(remoteAddress),
            _ => true
        };

    private static bool IsLoopback(IPAddress? address) => address is not null
        && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>Prevents anonymous DNS rebinding by matching the HTTP authority to the receiving socket.</summary>
    /// <param name="host">The untrusted HTTP Host header.</param>
    /// <param name="localAddress">The concrete local address reported by Kestrel.</param>
    /// <param name="localPort">The bound port. An omitted Host port denotes HTTP port 80.</param>
    /// <returns>True for a matching IP literal, or localhost on a loopback socket.</returns>
    internal static bool IsAllowedAnonymousHost(HostString host, IPAddress? localAddress, int localPort)
    {
        if (!host.HasValue || localAddress is null)
        {
            return false;
        }

        var name = host.Host;
        var port = host.Port;
        // HostString treats malformed ports as absent and tolerates suffixes after ']'.
        // Only accept a complete authority with an optional, successfully parsed port.
        if (!string.Equals(host.Value, name, StringComparison.Ordinal)
            && !(port.HasValue && host.Value.StartsWith(name + ":", StringComparison.Ordinal)))
        {
            return false;
        }
        if ((port ?? 80) != localPort)
        {
            return false;
        }

        localAddress = localAddress.IsIPv4MappedToIPv6 ? localAddress.MapToIPv4() : localAddress;
        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.IsLoopback(localAddress);
        }
        if (!IPAddress.TryParse(name, out var address))
        {
            return false;
        }
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
            && address.Equals(localAddress);
    }
}
