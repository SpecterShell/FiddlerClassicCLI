// Verifies metadata-only diagnostic exports using isolated delegates and uniquely named daemon peers.
using System.IO.Pipes;
using System.Text.Json;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Daemon;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class DiagnosticExportServiceTests : IDisposable
{
    private const string Secret = "DIAGNOSTIC_SECRET_SENTINEL";
    private readonly string _directory = Directory.CreateTempSubdirectory("fiddler-diagnostic-tests-").FullName;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExportsOnlyAllowlistedFieldsAndKeepsStdoutSeparate()
    {
        var rawStatus = new StatusResponse
        {
            FiddlerInstalled = true,
            FiddlerRunning = true,
            FiddlerVersion = "6.0.1.0",
            FiddlerPath = @"C:\Users\" + Secret + @"\Fiddler.exe",
            FiddlerProcessId = 123456789,
            BridgeInstalled = true,
            BridgeConnected = true,
            BridgeVersion = "0.3.0.0",
            IsProxyAttached = true,
            IsListening = true,
            IsHttpsDecryptionEnabled = true,
            ListenPort = 12345,
            SessionCount = 999999,
            CompletedSessionCount = 888888
        };
        var rawDaemon = RunningDaemon();
        rawDaemon.ProcessId = 234567890;
        rawDaemon.PipeName = Secret;
        rawDaemon.StartedAtUtc = Secret;
        rawDaemon.HostVersion = "0.3.0-preview.1+" + Secret;
        rawDaemon.Capabilities = [Secret, DaemonProtocol.ManagedHttpCapability, DaemonProtocol.ManagedHttpCapability];
        rawDaemon.HttpService!.Endpoint = "https://" + Secret + "/?token=" + Secret;
        rawDaemon.HttpService.LoopbackEndpoint = Secret;
        rawDaemon.HttpService.LanEndpoints = ["http://198.51.100.42:8877/mcp", Secret];
        rawDaemon.HttpService.BindAddresses = ["198.51.100.42", Secret];
        rawDaemon.HttpService.Endpoints = [Secret];
        rawDaemon.HttpService.AvailableInterfaces = [new() { Address = Secret, AdapterName = Secret }];
        rawDaemon.HttpService.StartupMode = Secret;
        rawDaemon.HttpService.AuthenticationMode = Secret;
        rawDaemon.HttpService.LastError = "Authorization: Bearer " + Secret;
        rawDaemon.HttpService.ActiveConnectionCount = 777777;
        var service = new DiagnosticExportService(
            _ => Task.FromResult(new StatusProbe(rawStatus, ErrorCodes.ProtocolMismatch, Secret)),
            _ => Task.FromResult<DaemonStatus?>(rawDaemon));
        var destination = Path.Combine(_directory, Secret + ".json");
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        DiagnosticExportResult receipt;
        try
        {
            Console.SetOut(output);
            receipt = await service.ExportAsync(destination, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(destination, receipt.Path);
        Assert.Equal("json", receipt.Format);
        var json = await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Secret, json);
        Assert.DoesNotContain("198.51.100.42", json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["schemaVersion", "hostVersion", "expectedBridgeProtocolVersion", "expectedDaemonProtocolVersion", "fiddler", "daemon", "errors"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["installed", "running", "discoveredVersion", "bridgeInstalled", "bridgeConnected", "bridgeVersion"],
            root.GetProperty("fiddler").EnumerateObject().Select(property => property.Name));
        var daemon = root.GetProperty("daemon");
        Assert.Equal(["running", "hostVersion", "capabilities", "listener"],
            daemon.EnumerateObject().Select(property => property.Name));
        Assert.Equal("0.3.0", daemon.GetProperty("hostVersion").GetString());
        Assert.Equal([DaemonProtocol.ManagedHttpCapability],
            daemon.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["enabled", "running", "bindMode", "bindAddress", "port", "startupMode", "authenticationMode"],
            daemon.GetProperty("listener").EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, root.GetProperty("errors").GetArrayLength());
    }

    [Theory]
    [InlineData(HttpBindModes.Loopback, "127.0.0.1", 8877)]
    [InlineData(HttpBindModes.All, "0.0.0.0", 65535)]
    public async Task PreservesKnownVersionsProtocolsAndListenerMetadata(string bindMode, string bindAddress, int port)
    {
        var daemon = RunningDaemon();
        daemon.HttpService!.BindMode = bindMode;
        daemon.HttpService.BindAddress = bindAddress;
        daemon.HttpService.Port = port;
        var report = await CreateService(daemon).CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(ProtocolConstants.Version, report.ExpectedBridgeProtocolVersion);
        Assert.Equal(DaemonProtocol.Version, report.ExpectedDaemonProtocolVersion);
        Assert.NotNull(report.HostVersion);
        Assert.Equal("6.0.0.0", report.Fiddler!.DiscoveredVersion);
        Assert.Equal("0.3.0.0", report.Fiddler.BridgeVersion);
        Assert.True(report.Daemon.Running);
        Assert.True(report.Daemon.Listener.Enabled);
        Assert.True(report.Daemon.Listener.Running);
        Assert.Equal(bindMode, report.Daemon.Listener.BindMode);
        Assert.Equal(bindAddress, report.Daemon.Listener.BindAddress);
        Assert.Equal(port, report.Daemon.Listener.Port);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task UntrustedStringsAndOutOfRangePortsAreOmitted()
    {
        var daemon = RunningDaemon();
        daemon.HostVersion = Secret;
        daemon.Capabilities = [Secret];
        daemon.HttpService!.BindMode = Secret;
        daemon.HttpService.BindAddress = "198.51.100.42";
        daemon.HttpService.Endpoint = Secret;
        daemon.HttpService.LastError = Secret;
        daemon.HttpService.Port = -1;
        var status = HealthyStatus();
        status.Status.FiddlerVersion = Secret;
        status.Status.BridgeVersion = Secret;
        var service = new DiagnosticExportService(
            _ => Task.FromResult(status), _ => Task.FromResult<DaemonStatus?>(daemon));
        var report = await service.CollectAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Serialize(report, JsonOptions);
        Assert.DoesNotContain(Secret, json);
        Assert.DoesNotContain("198.51.100.42", json);
        Assert.Null(report.Fiddler!.DiscoveredVersion);
        Assert.Null(report.Fiddler.BridgeVersion);
        Assert.Null(report.Daemon.HostVersion);
        Assert.Empty(report.Daemon.Capabilities);
        Assert.Null(report.Daemon.Listener.BindMode);
        Assert.Null(report.Daemon.Listener.BindAddress);
        Assert.Null(report.Daemon.Listener.Port);
        Assert.Contains(report.Errors, error => error.Code == ErrorCodes.ProtocolMismatch);
        Assert.Contains(report.Errors, error => error.Component == "listener" && error.Code == ErrorCodes.Unavailable);
    }

    [Theory]
    [InlineData(ErrorCodes.Unavailable)]
    [InlineData(ErrorCodes.Timeout)]
    [InlineData(ErrorCodes.ProtocolMismatch)]
    [InlineData(ErrorCodes.InvalidRequest)]
    [InlineData(ErrorCodes.NotFound)]
    [InlineData(ErrorCodes.Conflict)]
    [InlineData(ErrorCodes.ConfirmationRequired)]
    [InlineData(ErrorCodes.Internal)]
    [InlineData(Secret)]
    public async Task RawExceptionCodesMessagesAndInnerErrorsNeverSerialize(string rawCode)
    {
        var service = new DiagnosticExportService(
            _ => throw new BridgeClientException(rawCode, Secret, new IOException(Secret)),
            _ => throw new DaemonClientException(rawCode, Secret, new IOException(Secret)));
        var report = await service.CollectAsync(TestContext.Current.CancellationToken);
        Assert.Null(report.Fiddler);
        Assert.Null(report.Daemon.Running);
        Assert.Null(report.Daemon.Listener.Running);
        Assert.Equal(2, report.Errors.Count);
        Assert.All(report.Errors, error =>
        {
            Assert.Equal(rawCode == Secret ? ErrorCodes.Internal : rawCode, error.Code);
            Assert.NotEmpty(error.Message);
            Assert.DoesNotContain(Secret, error.Message);
        });
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report, JsonOptions));
    }

    [Fact]
    public async Task UnexpectedProbeExceptionsAreSanitizedAndIndependent()
    {
        var daemonProbed = false;
        var service = new DiagnosticExportService(
            _ => throw new InvalidOperationException(Secret),
            _ =>
            {
                daemonProbed = true;
                return Task.FromResult<DaemonStatus?>(RunningDaemon());
            });
        var report = await service.CollectAsync(TestContext.Current.CancellationToken);
        Assert.True(daemonProbed);
        Assert.True(report.Daemon.Running);
        Assert.Equal(ErrorCodes.Internal, Assert.Single(report.Errors).Code);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report, JsonOptions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedDaemonHasUnknownConfigurationAndNoAdditionalProbes(bool explicitStoppedStatus)
    {
        var statusCalls = 0;
        var daemonCalls = 0;
        var service = new DiagnosticExportService(
            _ =>
            {
                statusCalls++;
                return Task.FromResult(new StatusProbe(new StatusResponse(), null, null));
            },
            _ =>
            {
                daemonCalls++;
                return Task.FromResult(explicitStoppedStatus ? new DaemonStatus { HttpService = new HttpServiceStatus { Enabled = true } } : null);
            });
        var report = await service.CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, statusCalls);
        Assert.Equal(1, daemonCalls);
        Assert.False(report.Fiddler!.Running);
        Assert.False(report.Daemon.Running);
        Assert.False(report.Daemon.Listener.Running);
        Assert.Null(report.Daemon.Listener.Enabled);
        Assert.Null(report.Daemon.Listener.BindMode);
        Assert.Null(report.Daemon.Listener.BindAddress);
        Assert.Null(report.Daemon.Listener.Port);
    }

    [Fact]
    public async Task ReadsOnlyStatusFromAnIsolatedDaemonPeer()
    {
        var pipeName = "diagnostic-export-tests-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var daemon = new DaemonClient("must-never-launch.exe", pipeName, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        var bridgeCalls = new List<string>();
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                bridgeCalls.Add(operation);
                Assert.Equal(Operations.GetStatus, operation);
                Assert.IsType<EmptyRequest>(request);
                return HealthyStatus().Status;
            }
        };
        var service = new DiagnosticExportService(
            async token => new StatusProbe(await bridge.SendAsync<EmptyRequest, StatusResponse>(Operations.GetStatus, new EmptyRequest(), token), null, null),
            daemon.TryGetStatusAsync);
        var collecting = service.CollectAsync(deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        var request = JsonSerializer.Deserialize<DaemonRequest>((await FrameCodec.ReadAsync(server, deadline.Token))!, JsonOptions)!;
        Assert.Equal(DaemonProtocol.Status, request.Method);
        Assert.Equal("{}", request.PayloadJson);
        await FrameCodec.WriteAsync(server, JsonSerializer.Serialize(new DaemonResponse
        {
            RequestId = request.RequestId,
            Success = true,
            PayloadJson = JsonSerializer.Serialize(RunningDaemon(), JsonOptions)
        }, JsonOptions), deadline.Token);
        var report = await collecting;
        Assert.Equal([Operations.GetStatus], bridgeCalls);
        Assert.True(report.Daemon.Running);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task AbsentIsolatedDaemonIsNeverStarted()
    {
        var daemon = new DaemonClient("must-never-launch.exe", "diagnostic-absent-" + Guid.NewGuid().ToString("N"),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var service = new DiagnosticExportService(_ => Task.FromResult(HealthyStatus()), daemon.TryGetStatusAsync);
        var report = await service.CollectAsync(TestContext.Current.CancellationToken);
        Assert.False(report.Daemon.Running);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task ExistingDestinationIsUnchangedAndNotProbed()
    {
        var destination = Path.Combine(_directory, "existing.json");
        await File.WriteAllTextAsync(destination, Secret, TestContext.Current.CancellationToken);
        var service = new DiagnosticExportService(_ => throw new InvalidOperationException("Unexpected probe."),
            _ => throw new InvalidOperationException("Unexpected probe."));
        var error = await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(destination, TestContext.Current.CancellationToken));
        Assert.Contains("already exists", error.Message);
        Assert.Equal(Secret, await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task DestinationCreatedDuringCollectionIsNotOverwritten()
    {
        var destination = Path.Combine(_directory, "race.json");
        var service = new DiagnosticExportService(
            async token =>
            {
                await File.WriteAllTextAsync(destination, Secret, token);
                return HealthyStatus();
            },
            _ => Task.FromResult<DaemonStatus?>(null));
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(Secret, await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("report.json")]
    [InlineData("C:report.json")]
    [InlineData(@"\report.json")]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"\\?\C:\report.json")]
    [InlineData(@"C:\NUL.json")]
    [InlineData(@"C:\COM1.json")]
    [InlineData(@"C:\report.json:secret")]
    [InlineData(@"C:\report.json ")]
    [InlineData(@"C:\bad*name.json")]
    public async Task InvalidPathsAreRejectedBeforeProbing(string destination)
    {
        var probes = 0;
        var service = new DiagnosticExportService(
            _ => { probes++; return Task.FromResult(HealthyStatus()); },
            _ => { probes++; return Task.FromResult<DaemonStatus?>(null); });
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(0, probes);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task MissingParentDoesNotCreateDirectoriesOrExposePathsInErrors()
    {
        var destination = Path.Combine(_directory, Secret, "report.json");
        var error = await Assert.ThrowsAsync<IOException>(() => CreateService().ExportAsync(destination, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task CancellationIsPassedToBothProbesAndDoesNotCreateOutput()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var service = new DiagnosticExportService(
            token =>
            {
                Assert.Equal(cancellation.Token, token);
                return Task.FromResult(HealthyStatus());
            },
            token =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                return Task.FromResult<DaemonStatus?>(RunningDaemon());
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(Path.Combine(_directory, "cancelled.json"), cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task PreCancelledCollectionDoesNotProbe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probes = 0;
        var service = new DiagnosticExportService(
            _ => { probes++; return Task.FromResult(HealthyStatus()); },
            _ => { probes++; return Task.FromResult<DaemonStatus?>(null); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CollectAsync(cancellation.Token));
        Assert.Equal(0, probes);
    }

    private static DiagnosticExportService CreateService(DaemonStatus? daemon = null) => new(
        _ => Task.FromResult(HealthyStatus()), _ => Task.FromResult(daemon));

    private static StatusProbe HealthyStatus() => new(new StatusResponse
    {
        FiddlerInstalled = true,
        FiddlerRunning = true,
        FiddlerVersion = "6.0.0.0",
        BridgeInstalled = true,
        BridgeConnected = true,
        BridgeVersion = "0.3.0.0"
    }, null, null);

    private static DaemonStatus RunningDaemon() => new()
    {
        Running = true,
        HostVersion = "0.3.0-preview.1",
        Capabilities = [DaemonProtocol.ManagedHttpCapability],
        HttpService = new HttpServiceStatus { Enabled = true, Running = true }
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
