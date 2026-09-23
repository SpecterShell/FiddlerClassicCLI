// Ensures unhealthy daemons never permit offline administrative reads or mutations.
using System.IO.Pipes;
using System.Text.Json;
using FiddlerClassicCLI.Host.Daemon;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class DaemonDiscoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FiddlerClassicCLITests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "daemon-discovery-test-" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData("status")]
    [InlineData("configure")]
    [InlineData("enable")]
    [InlineData("disable")]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    [InlineData("clients")]
    [InlineData("connections")]
    [InlineData("disconnect")]
    public async Task ConnectedButSilentDaemonNeverFallsBackToConfiguration(string operation)
    {
        var store = new ConfigStore(_directory);
        var existing = store.AuthorizeClient("Existing client");
        store.SetHttpServiceEnabled(true);
        var before = File.ReadAllText(store.ConfigPath);
        using var server = CreateServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var administration = new HttpAdminClient(CreateClient(), store);
        var action = operation switch
        {
            "status" => administration.GetServiceStatusAsync(deadline.Token),
            "configure" => administration.ConfigureServiceAsync(new ConfigureHttpServiceRequest { BindMode = HttpBindModes.Loopback, Port = 9001 }, deadline.Token),
            "enable" => administration.EnableServiceAsync(true, deadline.Token),
            "disable" => administration.DisableServiceAsync(true, deadline.Token),
            "authorize" => administration.AuthorizeClientAsync("Unexpected client", deadline.Token),
            "deauthorize" => administration.DeauthorizeClientAsync(existing.Client.ClientId, true, deadline.Token),
            "clients" => administration.ListClientsAsync(deadline.Token),
            "connections" => administration.ListConnectionsAsync(deadline.Token),
            "disconnect" => (Task)administration.DisconnectAsync("connection", true, deadline.Token),
            _ => throw new InvalidOperationException()
        };
        await server.WaitForConnectionAsync(deadline.Token);
        Assert.NotNull(await FrameCodec.ReadAsync(server, deadline.Token));
        var error = await Assert.ThrowsAsync<DaemonClientException>(() => action);
        Assert.Equal(ErrorCodes.Timeout, error.Code);
        Assert.True(before == File.ReadAllText(store.ConfigPath), "An unresponsive daemon must not trigger configuration fallback.");
    }

    [Fact]
    public async Task BusyOwnerWithoutAControlListenerIsNotStopped()
    {
        using var owner = DaemonOwnership.TryAcquire(_pipeName);
        Assert.NotNull(owner);
        var error = await Assert.ThrowsAsync<DaemonClientException>(() => CreateClient().TryGetStatusAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.Timeout, error.Code);
    }

    [Fact]
    public async Task ConfirmsAbsenceAndKeepsOwnershipDuringOfflineAccess()
    {
        var client = CreateClient();
        Assert.Null(await client.TryGetStatusAsync(TestContext.Current.CancellationToken));
        using (client.AcquireOfflineAccess())
        {
            using var competingOwner = DaemonOwnership.TryAcquire(_pipeName);
            Assert.Null(competingOwner);
            Assert.Equal(ErrorCodes.Unavailable,
                Assert.Throws<DaemonClientException>(() => client.AcquireOfflineAccess()).Code);
        }
        using var availableAgain = DaemonOwnership.TryAcquire(_pipeName);
        Assert.NotNull(availableAgain);
    }

    [Theory]
    [InlineData("{", ErrorCodes.Unavailable)]
    [InlineData("", ErrorCodes.Unavailable)]
    [InlineData("error", ErrorCodes.Timeout)]
    public async Task MalformedOrRejectedStatusIsNotAbsence(string payload, string expectedCode)
    {
        using var server = CreateServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var probe = CreateClient().TryGetStatusAsync(deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        var request = JsonSerializer.Deserialize<DaemonRequest>((await FrameCodec.ReadAsync(server, deadline.Token))!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (payload == "error")
        {
            payload = JsonSerializer.Serialize(new DaemonResponse
            {
                RequestId = request.RequestId,
                Success = false,
                Error = new BridgeError { Code = ErrorCodes.Timeout, Message = "Configuration is busy." }
            });
        }
        if (payload.Length == 0)
        {
            server.Disconnect();
        }
        else
        {
            await FrameCodec.WriteAsync(server, payload, deadline.Token);
        }
        var error = await Assert.ThrowsAsync<DaemonClientException>(() => probe);
        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public async Task PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateClient().TryGetStatusAsync(cancellation.Token));
    }

    private DaemonClient CreateClient() => new("must-not-launch.exe", _pipeName, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    private NamedPipeServerStream CreateServer() => new(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
