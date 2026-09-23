// Verifies managed HTTP CLI output, stopped-daemon behavior, and confirmation boundaries.
using System.CommandLine;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FiddlerClassicCLI.Host.Cli;
using FiddlerClassicCLI.Host.Daemon;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class ManagedHttpCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "FiddlerClassicCLITests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void StatusAndDisableDoNotStartAStoppedDaemon()
    {
        var (root, store) = CreateRoot();
        store.SetHttpServiceEnabled(true);

        var status = Invoke(root, "mcp", "service", "status", "--json");
        var disabled = Invoke(root, "mcp", "service", "disable", "--yes", "--json");

        Assert.Equal(0, status.ExitCode);
        using (var statusJson = JsonDocument.Parse(status.StandardOutput))
        {
            Assert.True(statusJson.RootElement.GetProperty("enabled").GetBoolean());
            Assert.False(statusJson.RootElement.GetProperty("running").GetBoolean());
        }

        Assert.Equal(0, disabled.ExitCode);
        Assert.False(store.GetOrCreate().HttpServiceEnabled);
    }

    [Fact]
    public async Task UnresponsiveDaemonReturnsTimeoutWithoutOfflineDisable()
    {
        var pipeName = $"fiddler-classic-cli.http-tests.{Guid.NewGuid():N}";
        var (root, store) = CreateRoot(pipeName);
        store.SetHttpServiceEnabled(true);
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var invocation = Task.Run(() => Invoke(root, "mcp", "service", "disable", "--yes", "--json"), deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        Assert.NotNull(await FrameCodec.ReadAsync(server, deadline.Token));

        var result = await invocation;

        Assert.Equal(ExitCodes.Timeout, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(ErrorCodes.Timeout, result.StandardError, StringComparison.Ordinal);
        Assert.True(store.GetOrCreate().HttpServiceEnabled);
    }

    [Fact]
    public void RemoteEnablementRequiresNonInteractiveConfirmation()
    {
        var (root, store) = CreateRoot();
        store.ConfigureHttpService(HttpBindModes.All, 8877);

        var result = Invoke(root, "mcp", "service", "enable", "--json");

        Assert.Equal(ExitCodes.Rejected, result.ExitCode);
        Assert.Contains(ErrorCodes.ConfirmationRequired, result.StandardError, StringComparison.Ordinal);
        Assert.False(store.GetOrCreate().HttpServiceEnabled);
    }

    [Fact]
    public void AuthorizationShowsTheTokenOnceAndListingsOmitIt()
    {
        var (root, _) = CreateRoot();

        var authorization = Invoke(root, "mcp", "clients", "authorize", "--name", "test-agent", "--json");
        Assert.Equal(0, authorization.ExitCode);
        using var authorizationJson = JsonDocument.Parse(authorization.StandardOutput);
        var token = authorizationJson.RootElement.GetProperty("token").GetString();
        var clientId = authorizationJson.RootElement.GetProperty("client").GetProperty("clientId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var clients = Invoke(root, "mcp", "clients", "list", "--json");
        var connections = Invoke(root, "mcp", "connections", "list", "--json");
        var rejected = Invoke(root, "mcp", "clients", "deauthorize", clientId!, "--json");

        Assert.Equal(0, clients.ExitCode);
        Assert.DoesNotContain(token!, clients.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, connections.ExitCode);
        using (var connectionJson = JsonDocument.Parse(connections.StandardOutput))
        {
            Assert.Equal(0, connectionJson.RootElement.GetProperty("connections").GetArrayLength());
        }

        Assert.Equal(ExitCodes.Rejected, rejected.ExitCode);
        Assert.Contains(ErrorCodes.ConfirmationRequired, rejected.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ForegroundServerReportsAPortConflictClearly()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var (root, _) = CreateRoot();

        var result = Invoke(root, "mcp", "http", "--port", port.ToString(), "--json");

        Assert.Equal(ExitCodes.Unavailable, result.ExitCode);
        Assert.Contains("managed MCP HTTP service", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("listening on", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private (RootCommand Root, ConfigStore Store) CreateRoot(string? pipeName = null)
    {
        var bridge = new TestBridgeClient();
        var environment = new FiddlerEnvironment();
        var store = new ConfigStore(_directory);
        var actions = new CliActions(
            bridge,
            new BridgeInstaller(environment),
            store,
            environment,
            new StatusService(bridge, environment));
        var daemon = new DaemonClient(
            "unused.exe",
            pipeName ?? $"fiddler-classic-cli.http-tests.{Guid.NewGuid():N}",
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
        return (CommandFactory.Create(actions, daemon), store);
    }

    private static InvocationResult Invoke(RootCommand root, params string[] arguments)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var input = new StringReader(string.Empty);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Console.SetIn(input);
            var exitCode = root.Parse(arguments).Invoke();
            return new InvocationResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed record InvocationResult(int ExitCode, string StandardOutput, string StandardError);
}
