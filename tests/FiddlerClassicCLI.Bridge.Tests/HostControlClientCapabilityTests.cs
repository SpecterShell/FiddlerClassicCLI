// Rejects older daemon settings contracts before mutations and preserves authentication-only requests.
using System.IO.Pipes;
using System.Web.Script.Serialization;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class HostControlClientTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("configure")]
    [InlineData("startup")]
    [InlineData("enable")]
    [InlineData("disable")]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    [InlineData("disconnect")]
    public async Task OlderDaemonIsRejectedBeforeAnyHttpMutation(string operation)
    {
        using var server = CreateServer(out var pipeName);
        var client = new HostControlClient(pipeName, TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task exchange = operation switch
        {
            "status" => client.GetServiceStatusAsync(deadline.Token),
            "configure" => client.ConfigureServiceAsync(new ConfigureHttpServiceRequest
                { AuthenticationMode = HttpAuthenticationModes.None, Confirm = true }, deadline.Token),
            "startup" => client.ApplyStartupAsync(deadline.Token),
            "enable" => client.EnableServiceAsync(true, deadline.Token),
            "disable" => client.DisableServiceAsync(true, deadline.Token),
            "authorize" => client.AuthorizeClientAsync("Test client", deadline.Token),
            "deauthorize" => client.DeauthorizeClientAsync("default", deadline.Token),
            _ => client.DisconnectAsync("test-connection", deadline.Token)
        };
        await RespondToCapabilityCheckAsync(server, "managed-http-v2", deadline.Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => exchange);
        Assert.Contains("does not support the current MCP HTTP settings", error.Message);
        Assert.Contains("Install the matching CLI and bridge", error.Message);
        Assert.Contains("daemon stop", error.Message);
    }

    [Theory]
    [InlineData(HttpAuthenticationModes.Required)]
    [InlineData(HttpAuthenticationModes.NonLoopback)]
    [InlineData(HttpAuthenticationModes.None)]
    public async Task AuthenticationOnlySaveDoesNotSendBindingValues(string mode)
    {
        using var server = CreateServer(out var pipeName);
        var client = new HostControlClient(pipeName, TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exchange = client.ConfigureServiceAsync(new ConfigureHttpServiceRequest
            { AuthenticationMode = mode, Confirm = mode == HttpAuthenticationModes.None }, deadline.Token);
        await RespondToCapabilityCheckAsync(server, DaemonProtocol.ManagedHttpCapability, deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        var serializer = new JavaScriptSerializer();
        var request = serializer.Deserialize<DaemonRequest>(await FrameCodec.ReadAsync(server, deadline.Token));
        Assert.Equal(DaemonProtocol.ConfigureHttpService, request.Method);
        var settings = serializer.Deserialize<ConfigureHttpServiceRequest>(request.PayloadJson);
        Assert.Null(settings.BindMode);
        Assert.Null(settings.BindAddresses);
        Assert.Null(settings.Port);
        Assert.Null(settings.StartupMode);
        Assert.Equal("managed-http-v3", DaemonProtocol.ManagedHttpCapability);
        Assert.Equal(mode, settings.AuthenticationMode);
        Assert.DoesNotContain("RequireAuthentication", request.PayloadJson);
        Assert.Equal(mode == HttpAuthenticationModes.None, settings.Confirm);
        await FrameCodec.WriteAsync(server, serializer.Serialize(new DaemonResponse
        {
            RequestId = request.RequestId, Success = true,
            PayloadJson = serializer.Serialize(new HttpServiceStatus { AuthenticationMode = mode })
        }), deadline.Token);
        Assert.Equal(mode, (await exchange).AuthenticationMode);
    }

    private static async Task RespondToCapabilityCheckAsync(
        NamedPipeServerStream server, string capability, CancellationToken cancellationToken)
    {
        await server.WaitForConnectionAsync(cancellationToken);
        var serializer = new JavaScriptSerializer();
        var request = serializer.Deserialize<DaemonRequest>(await FrameCodec.ReadAsync(server, cancellationToken));
        Assert.Equal(DaemonProtocol.Status, request.Method);
        await FrameCodec.WriteAsync(server, serializer.Serialize(new DaemonResponse
        {
            RequestId = request.RequestId, Success = true,
            PayloadJson = serializer.Serialize(new DaemonStatus
            {
                HostVersion = "test-daemon", Capabilities = new[] { capability }, HttpService = new HttpServiceStatus()
            })
        }), cancellationToken);
        server.Disconnect();
    }
}
