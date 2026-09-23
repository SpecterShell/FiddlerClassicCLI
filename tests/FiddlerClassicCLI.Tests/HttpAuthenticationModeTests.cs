// Checks per-connection authentication boundaries without trusting caller-supplied address headers.
using System.Net;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Protocol;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;

namespace FiddlerClassicCLI.Tests;

public sealed class HttpAuthenticationModeTests
{
    [Theory]
    [InlineData(HttpAuthenticationModes.Required, "127.0.0.1", "127.0.0.1", true)]
    [InlineData(HttpAuthenticationModes.None, "192.0.2.1", "192.0.2.2", false)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "127.0.0.1", "127.0.0.2", false)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "::1", "::1", false)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "::ffff:127.0.0.1", "::ffff:127.0.0.2", false)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "192.0.2.1", "192.0.2.1", true)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "127.0.0.1", "192.0.2.2", true)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "192.0.2.1", "127.0.0.1", true)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "::ffff:192.0.2.1", "::ffff:192.0.2.2", true)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "::ffff:127.0.0.1", "::ffff:192.0.2.2", true)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "127.0.0.1", null, true)]
    [InlineData(HttpAuthenticationModes.NonLoopback, null, "127.0.0.1", true)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "0.0.0.0", "127.0.0.1", true)]
    [InlineData(HttpAuthenticationModes.NonLoopback, "127.0.0.1", "::", true)]
    [InlineData("unknown", "127.0.0.1", "127.0.0.1", true)]
    public void ExemptionRequiresBothSocketEndpointsToBeLoopback(string mode, string? local, string? remote, bool required)
    {
        Assert.Equal(required, HttpAccessPolicy.RequiresAuthentication(mode, Parse(local), Parse(remote)));
    }

    [Theory]
    [InlineData("127.0.0.1", null, null, 200)]
    [InlineData("::ffff:127.0.0.1", null, null, 200)]
    [InlineData("192.0.2.2", null, null, 401)]
    [InlineData("::ffff:192.0.2.2", "invalid", null, 401)]
    [InlineData(null, null, null, 401)]
    [InlineData("192.0.2.2", HttpPolicyTestServer.Credential, null, 200)]
    [InlineData("192.0.2.2", HttpPolicyTestServer.Credential, "https://untrusted.example", 403)]
    [InlineData("127.0.0.1", null, "null", 403)]
    public async Task MiddlewareUsesSocketMetadataAndKeepsOriginProtection(
        string? remote, string? token, string? origin, int expectedStatus)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Host = new HostString("localhost", 8877);
        context.Request.Headers["Forwarded"] = "for=127.0.0.1;host=localhost";
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        context.Request.Headers["X-Real-IP"] = "127.0.0.1";
        if (token is not null) context.Request.Headers.Authorization = "Bearer " + token;
        if (origin is not null) context.Request.Headers.Origin = origin;
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.RemoteIpAddress = Parse(remote);
        context.Connection.LocalPort = 8877;
        context.Connection.Id = "test-connection";
        var credentials = new HttpPolicyTestServer.CountingCredentials();
        var records = new HttpConnectionRegistry();
        await using var connection = new DefaultConnectionContext(context.Connection.Id);
        var dispatched = false;
        await records.TrackAsync(connection, async _ =>
        {
            await HttpAccessPolicy.InvokeAsync(context, _ =>
            {
                dispatched = true;
                return Task.CompletedTask;
            }, HttpAuthenticationModes.NonLoopback, credentials, records);
            var record = Assert.Single(records.List().Connections);
            Assert.Equal(1, record.TotalRequestCount);
            Assert.Equal(0, record.ActiveRequestCount);
            if (credentials.Calls == 0)
            {
                Assert.Empty(record.ClientIds);
                Assert.Empty(record.ClientNames);
                Assert.Equal(expectedStatus == 200 ? "anonymous" : "rejected", record.AuthorizationState);
            }
        });
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal(expectedStatus == 200, dispatched);
        Assert.False(context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.Equal(expectedStatus == 401, context.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonLoopbackModeAllowsLocalKestrelRequestsWithHostAndOriginChecks(bool allInterfaces)
    {
        await using var server = await HttpPolicyTestServer.StartAsync(
            addresses: [allInterfaces ? IPAddress.Any : IPAddress.Loopback],
            authenticationMode: HttpAuthenticationModes.NonLoopback);
        using var request = HttpPolicyTestServer.Request();
        using var accepted = await server.Client.SendAsync(request, server.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("anonymous", Assert.Single(server.Connections.List().Connections).AuthorizationState);
        Assert.Equal(0, server.Credentials.Calls);
        using var rebound = HttpPolicyTestServer.Request();
        rebound.Headers.Host = $"untrusted.example:{server.Port}";
        using var forbidden = await server.Client.SendAsync(rebound, server.Token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var browser = HttpPolicyTestServer.Request();
        browser.Headers.Add("Origin", "http://localhost");
        using var rejected = await server.Client.SendAsync(browser, server.Token);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
    }

    private static IPAddress? Parse(string? text) => text is null ? null : IPAddress.Parse(text);
}
