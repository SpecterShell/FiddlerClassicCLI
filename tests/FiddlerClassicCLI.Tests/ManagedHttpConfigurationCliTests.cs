// Exercises managed HTTP CLI settings, help, and confirmations with isolated configuration and fake daemon peers.
using System.IO.Pipes;
using System.Text.Json;
using FiddlerClassicCLI.Host.Cli;
using FiddlerClassicCLI.Host.Daemon;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed partial class ManagedHttpCliTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("--yes")]
    [InlineData("--bind")]
    [InlineData("--bind invalid")]
    [InlineData("--bind selected")]
    [InlineData("--bind all --address 127.0.0.1")]
    [InlineData("--bind loopback --address 127.0.0.1")]
    [InlineData("--address")]
    [InlineData("--address ::1")]
    [InlineData("--address localhost")]
    [InlineData("--address 127.1")]
    [InlineData("--address 127.0.0.999")]
    [InlineData("--address 0.0.0.0")]
    [InlineData("--address 224.0.0.1")]
    [InlineData("--address 255.255.255.255")]
    [InlineData("--startup")]
    [InlineData("--startup last")]
    [InlineData("--authentication")]
    [InlineData("--authentication optional")]
    [InlineData("--authentication true")]
    [InlineData("--authentication false")]
    [InlineData("--port 0")]
    [InlineData("--port 65536")]
    public void ConfigureRejectsInvalidOptionsBeforeInvocation(string options)
    {
        var (root, store) = CreateRoot();
        var parsed = root.Parse(("mcp service configure " + options + " --json").Split(' '));

        Assert.NotEmpty(parsed.Errors);
        Assert.False(File.Exists(store.ConfigPath));
    }

    [Fact]
    public void SelectedAddressesAreBoundedToSixteen()
    {
        var (root, _) = CreateRoot();
        var arguments = new[] { "mcp", "service", "configure", "--bind", "selected" }
            .Concat(Enumerable.Range(1, 17).SelectMany(index => new[] { "--address", $"127.0.0.{index}" }))
            .ToArray();

        Assert.Contains(root.Parse(arguments).Errors, error => error.Message.Contains("16", StringComparison.Ordinal));
        Assert.Empty(root.Parse(arguments[..^2]).Errors);
    }

    [Fact]
    public void ConfigureHelpDescribesListenerSettingsWithoutReadingConfiguration()
    {
        var (root, store) = CreateRoot();
        var result = Invoke(root, "mcp", "service", "configure", "--help");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        foreach (var option in new[] { "--bind", "--address", "--port", "--startup", "--authentication", "--yes" })
            Assert.Contains(option, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Disable the service", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("last-state", result.StandardOutput, StringComparison.Ordinal);
        foreach (var mode in new[] { HttpAuthenticationModes.Required, HttpAuthenticationModes.NonLoopback, HttpAuthenticationModes.None })
            Assert.Contains(mode, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("16", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(store.ConfigPath));
    }

    [Fact]
    public void OfflineSelectedAddressUpdatesPreserveModeAndExposeAllEndpoints()
    {
        var (root, store) = CreateRoot();
        var configured = ReadStatus(Invoke(root, "mcp", "service", "configure", "--bind", "selected",
            "--address", "127.0.0.1", "--address", "127.0.0.2", "--port", "9123", "--json"));

        Assert.Equal(HttpBindModes.Selected, configured.BindMode);
        Assert.Equal(["127.0.0.1", "127.0.0.2"], configured.BindAddresses);
        Assert.Equal(["http://127.0.0.1:9123/mcp", "http://127.0.0.2:9123/mcp"], configured.Endpoints);
        Assert.Equal(configured.BindAddresses[0], configured.BindAddress);
        Assert.Equal(configured.Endpoints[0], configured.Endpoint);
        Assert.False(configured.Running);
        Assert.False(configured.Enabled);

        var updated = ReadStatus(Invoke(root, "mcp", "service", "configure", "--address", "127.0.0.3", "--json"));
        Assert.Equal(HttpBindModes.Selected, updated.BindMode);
        Assert.Equal(["127.0.0.3"], updated.BindAddresses);
        Assert.Equal(9123, updated.Port);

        var status = Invoke(root, "mcp", "service", "status", "--json");
        using var json = JsonDocument.Parse(status.StandardOutput);
        Assert.Equal(HttpStartupModes.LastState, json.RootElement.GetProperty("startupMode").GetString());
        Assert.Equal(HttpAuthenticationModes.NonLoopback, json.RootElement.GetProperty("authenticationMode").GetString());
        Assert.False(json.RootElement.TryGetProperty("requireAuthentication", out _));
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("availableInterfaces").ValueKind);
        Assert.False(status.StandardOutput.Contains(store.GetOrCreate().HttpBearerToken, StringComparison.Ordinal),
            "Service status must omit credentials.");
    }

    [Fact]
    public void AddressUpdateRequiresSavedSelectedMode()
    {
        var (root, store) = CreateRoot();
        var result = Invoke(root, "mcp", "service", "configure", "--address", "127.0.0.1", "--json");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(ErrorCodes.InvalidRequest, result.StandardError, StringComparison.Ordinal);
        Assert.Equal(HttpBindModes.Loopback, store.GetOrCreate().HttpBindMode);
    }

    [Theory]
    [InlineData(HttpStartupModes.Enabled)]
    [InlineData(HttpStartupModes.Disabled)]
    [InlineData(HttpStartupModes.LastState)]
    public void OfflineStartupPolicyDoesNotStartDaemonOrChangeSavedEnabledState(string mode)
    {
        var (root, store) = CreateRoot();
        store.SetHttpServiceEnabled(true);
        var configured = ReadStatus(Invoke(root, "mcp", "service", "configure", "--startup", mode, "--json"));

        Assert.Equal(mode, configured.StartupMode);
        Assert.True(configured.Enabled);
        Assert.False(configured.Running);
        var disabled = ReadStatus(Invoke(root, "mcp", "service", "disable", "--json"));
        Assert.False(disabled.Enabled);
        Assert.Equal(mode, disabled.StartupMode);
    }

    [Theory]
    [InlineData(HttpBindModes.Loopback)]
    [InlineData(HttpBindModes.All)]
    public void DisablingAuthenticationAlwaysRequiresConfirmation(string mode)
    {
        var (root, store) = CreateRoot();
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { BindMode = mode });
        var rejected = Invoke(root, "mcp", "service", "configure", "--authentication", "none", "--json");

        Assert.Equal(ExitCodes.Rejected, rejected.ExitCode);
        Assert.Contains(ErrorCodes.ConfirmationRequired, rejected.StandardError, StringComparison.Ordinal);
        Assert.Contains("without a token", rejected.StandardError, StringComparison.Ordinal);
        Assert.Equal(HttpAuthenticationModes.NonLoopback,
            ReadStatus(Invoke(root, "mcp", "service", "status", "--json")).AuthenticationMode);

        var configured = ReadStatus(Invoke(root, "mcp", "service", "configure", "--authentication", "none", "--yes", "--json"));
        Assert.Equal(HttpAuthenticationModes.None, configured.AuthenticationMode);
        Assert.False(configured.Enabled);
        Assert.Equal(ExitCodes.Rejected,
            Invoke(root, "mcp", "service", "configure", "--authentication", "none", "--json").ExitCode);
        Assert.Equal(HttpAuthenticationModes.Required,
            ReadStatus(Invoke(root, "mcp", "service", "configure", "--authentication", "required", "--json")).AuthenticationMode);
        Assert.Equal(HttpAuthenticationModes.NonLoopback,
            ReadStatus(Invoke(root, "mcp", "service", "configure", "--authentication", "non-loopback", "--json")).AuthenticationMode);
    }

    [Theory]
    [InlineData(HttpBindModes.Loopback, HttpAuthenticationModes.Required)]
    [InlineData(HttpBindModes.Loopback, HttpAuthenticationModes.NonLoopback)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.Required)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.NonLoopback)]
    public void LoopbackAuthenticationCanBeSavedWithoutWarningEvenWithAutomaticStartup(string bindMode, string authenticationMode)
    {
        var (root, store) = CreateRoot();
        store.ConfigureHttpService(new ConfigureHttpServiceRequest
        {
            BindMode = bindMode,
            BindAddresses = bindMode == HttpBindModes.Selected ? ["127.0.0.1", "127.0.0.2"] : null,
            AuthenticationMode = HttpAuthenticationModes.None,
            StartupMode = HttpStartupModes.Enabled,
            Confirm = true
        });

        var configured = ReadStatus(Invoke(root, "mcp", "service", "configure", "--authentication", authenticationMode, "--json"));

        Assert.Equal(authenticationMode, configured.AuthenticationMode);
        Assert.Equal(HttpStartupModes.Enabled, configured.StartupMode);
        Assert.Equal(bindMode, configured.BindMode);
        Assert.False(configured.Enabled);
        Assert.False(configured.Running);
        Assert.Equal(authenticationMode, ReadStatus(Invoke(root, "mcp", "service", "status", "--json")).AuthenticationMode);
    }

    [Theory]
    [InlineData(HttpAuthenticationModes.Required)]
    [InlineData(HttpAuthenticationModes.NonLoopback)]
    public void UnsafeStartupRequiresConfirmationForPolicyAndBindingChanges(string authenticationMode)
    {
        var (root, _) = CreateRoot();
        ReadStatus(Invoke(root, "mcp", "service", "configure", "--bind", "all", "--authentication", authenticationMode, "--json"));
        var rejected = Invoke(root, "mcp", "service", "configure", "--startup", "enabled", "--json");
        Assert.Equal(ExitCodes.Rejected, rejected.ExitCode);
        Assert.Contains("without encryption", rejected.StandardError, StringComparison.Ordinal);
        Assert.Contains("automatically at startup", rejected.StandardError, StringComparison.Ordinal);
        ReadStatus(Invoke(root, "mcp", "service", "configure", "--startup", "enabled", "--yes", "--json"));

        ReadStatus(Invoke(root, "mcp", "service", "configure", "--bind", "loopback", "--json"));
        Assert.Equal(ExitCodes.Rejected, Invoke(root, "mcp", "service", "configure", "--bind", "all", "--json").ExitCode);
        ReadStatus(Invoke(root, "mcp", "service", "configure", "--bind", "all", "--yes", "--json"));
        ReadStatus(Invoke(root, "mcp", "service", "configure", "--startup", "disabled", "--json"));
    }

    [Fact]
    public void LoopbackWithoutAuthenticationRequiresConfirmationAtEveryEnableBoundary()
    {
        var (root, _) = CreateRoot();
        ReadStatus(Invoke(root, "mcp", "service", "configure", "--authentication", "none", "--yes", "--json"));
        var startup = Invoke(root, "mcp", "service", "configure", "--startup", "enabled", "--json");
        var enable = Invoke(root, "mcp", "service", "enable", "--json");

        foreach (var rejected in new[] { startup, enable })
        {
            Assert.Equal(ExitCodes.Rejected, rejected.ExitCode);
            Assert.Empty(rejected.StandardOutput);
            Assert.Contains("without a token", rejected.StandardError, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(HttpAuthenticationModes.Required, "required (bearer token for all clients)")]
    [InlineData(HttpAuthenticationModes.NonLoopback, "non-loopback (bearer token required outside loopback)")]
    [InlineData(HttpAuthenticationModes.None, "none (unauthenticated access)")]
    public async Task HumanStatusListsAuthenticationAndSelectedEndpointsWithoutFalseLoopbackUrl(string authenticationMode, string description)
    {
        var state = new HttpServiceStatus
        {
            BindMode = HttpBindModes.Selected,
            BindAddress = "192.0.2.10",
            BindAddresses = ["192.0.2.10", "192.0.2.11"],
            Endpoint = "http://192.0.2.10:8877/mcp",
            Endpoints = ["http://192.0.2.10:8877/mcp", "http://192.0.2.11:8877/mcp"],
            AvailableInterfaces = [new() { Address = "192.0.2.10", AdapterName = "Test Ethernet" }],
            StartupMode = HttpStartupModes.Disabled,
            AuthenticationMode = authenticationMode
        };
        var (result, _) = await InvokeWithDaemonAsync(state, ["mcp", "service", "status"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("Startup policy:       disabled", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"Authentication:       {description}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Test Ethernet", result.StandardOutput, StringComparison.Ordinal);
        Assert.All(state.Endpoints, endpoint => Assert.Contains(endpoint, result.StandardOutput, StringComparison.Ordinal));
        Assert.DoesNotContain("127.0.0.1", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpBindModes.All, HttpAuthenticationModes.Required)]
    [InlineData(HttpBindModes.All, HttpAuthenticationModes.NonLoopback)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.Required)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.NonLoopback)]
    [InlineData(HttpBindModes.Loopback, HttpAuthenticationModes.None)]
    [InlineData(HttpBindModes.All, HttpAuthenticationModes.None)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.None)]
    public async Task EnablePassesExplicitRiskConfirmationToDaemon(string mode, string authenticationMode)
    {
        var state = new HttpServiceStatus
        {
            BindMode = mode,
            BindAddresses = mode == HttpBindModes.Loopback ? ["127.0.0.1"] : ["127.0.0.1", "192.0.2.10"],
            AuthenticationMode = authenticationMode
        };
        var (rejected, rejectedRequests) = await InvokeWithDaemonAsync(state, ["mcp", "service", "enable", "--json"]);
        Assert.Equal(ExitCodes.Rejected, rejected.ExitCode);
        Assert.Empty(rejected.StandardOutput);
        Assert.Contains(ErrorCodes.ConfirmationRequired, rejected.StandardError, StringComparison.Ordinal);
        Assert.Contains(authenticationMode == HttpAuthenticationModes.None ? "without a token" : "without encryption",
            rejected.StandardError, StringComparison.Ordinal);
        Assert.All(rejectedRequests, request => Assert.Equal(DaemonProtocol.Status, request.Method));

        var (accepted, acceptedRequests) = await InvokeWithDaemonAsync(state, ["mcp", "service", "enable", "--yes", "--json"]);
        ReadStatus(accepted);
        var enable = Assert.Single(acceptedRequests, request => request.Method == DaemonProtocol.EnableHttpService);
        Assert.True(JsonSerializer.Deserialize<SetHttpServiceEnabledRequest>(enable.PayloadJson, WebJson)!.Confirm);
    }

    [Theory]
    [InlineData(HttpBindModes.Loopback, HttpAuthenticationModes.Required)]
    [InlineData(HttpBindModes.Loopback, HttpAuthenticationModes.NonLoopback)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.Required)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.NonLoopback)]
    public async Task LoopbackEnableNeedsNoRiskConfirmation(string bindMode, string authenticationMode)
    {
        var state = new HttpServiceStatus
        {
            BindMode = bindMode,
            BindAddresses = bindMode == HttpBindModes.Selected ? ["127.0.0.1", "127.0.0.2"] : ["127.0.0.1"],
            AuthenticationMode = authenticationMode
        };
        var (result, requests) = await InvokeWithDaemonAsync(state, ["mcp", "service", "enable", "--json"]);

        Assert.Equal(authenticationMode, ReadStatus(result).AuthenticationMode);
        var enable = Assert.Single(requests, request => request.Method == DaemonProtocol.EnableHttpService);
        Assert.False(JsonSerializer.Deserialize<SetHttpServiceEnabledRequest>(enable.PayloadJson, WebJson)!.Confirm);
        Assert.False(File.Exists(new ConfigStore(_directory).ConfigPath));
    }

    [Theory]
    [InlineData(HttpBindModes.All, HttpAuthenticationModes.Required)]
    [InlineData(HttpBindModes.All, HttpAuthenticationModes.NonLoopback)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.Required)]
    [InlineData(HttpBindModes.Selected, HttpAuthenticationModes.NonLoopback)]
    public async Task RemoteStartupConfigurationUsesResultingAuthenticationModeForWarning(string bindMode, string authenticationMode)
    {
        var arguments = new List<string>
        {
            "mcp", "service", "configure", "--bind", bindMode,
            "--authentication", authenticationMode, "--startup", "enabled", "--json"
        };
        if (bindMode == HttpBindModes.Selected)
            arguments.AddRange(["--address", "127.0.0.1", "192.0.2.10"]);
        var state = new HttpServiceStatus { AuthenticationMode = HttpAuthenticationModes.None };

        var (rejected, rejectedRequests) = await InvokeWithDaemonAsync(state, arguments.ToArray());

        Assert.Equal(ExitCodes.Rejected, rejected.ExitCode);
        Assert.Empty(rejected.StandardOutput);
        Assert.Contains(ErrorCodes.ConfirmationRequired, rejected.StandardError, StringComparison.Ordinal);
        Assert.Contains("without encryption", rejected.StandardError, StringComparison.Ordinal);
        Assert.Contains("automatically at startup", rejected.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("without a token", rejected.StandardError, StringComparison.Ordinal);
        Assert.All(rejectedRequests, request => Assert.Equal(DaemonProtocol.Status, request.Method));

        arguments.Add("--yes");
        var (accepted, acceptedRequests) = await InvokeWithDaemonAsync(state, arguments.ToArray());
        ReadStatus(accepted);
        var configure = Assert.Single(acceptedRequests, request => request.Method == DaemonProtocol.ConfigureHttpService);
        var sent = JsonSerializer.Deserialize<ConfigureHttpServiceRequest>(configure.PayloadJson, WebJson)!;
        Assert.Equal(authenticationMode, sent.AuthenticationMode);
        Assert.True(sent.Confirm);
        Assert.False(File.Exists(new ConfigStore(_directory).ConfigPath));
    }

    [Theory]
    [InlineData(HttpAuthenticationModes.Required)]
    [InlineData(HttpAuthenticationModes.NonLoopback)]
    [InlineData(HttpAuthenticationModes.None)]
    public async Task ConfigureForwardsCompleteRequestWithoutWritingOfflineConfiguration(string authenticationMode)
    {
        var (result, requests) = await InvokeWithDaemonAsync(new HttpServiceStatus(),
            ["mcp", "service", "configure", "--bind", "selected", "--address", "192.0.2.10", "192.0.2.11",
                "--port", "9100", "--startup", "enabled", "--authentication", authenticationMode, "--yes", "--json"]);
        ReadStatus(result);
        var configure = Assert.Single(requests, request => request.Method == DaemonProtocol.ConfigureHttpService);
        var sent = JsonSerializer.Deserialize<ConfigureHttpServiceRequest>(configure.PayloadJson, WebJson)!;
        Assert.Equal(HttpBindModes.Selected, sent.BindMode);
        Assert.Equal(["192.0.2.10", "192.0.2.11"], sent.BindAddresses!);
        Assert.Equal(9100, sent.Port);
        Assert.Equal(HttpStartupModes.Enabled, sent.StartupMode);
        Assert.Equal(authenticationMode, sent.AuthenticationMode);
        using var payload = JsonDocument.Parse(configure.PayloadJson);
        Assert.Equal(authenticationMode, payload.RootElement.GetProperty("authenticationMode").GetString());
        Assert.False(payload.RootElement.TryGetProperty("requireAuthentication", out _));
        Assert.True(sent.Confirm);
        Assert.False(File.Exists(new ConfigStore(_directory).ConfigPath));
    }

    [Theory]
    [InlineData(HttpStartupModes.Disabled, false)]
    [InlineData(HttpStartupModes.LastState, false)]
    [InlineData(HttpStartupModes.Enabled, true)]
    public async Task StartupOnlyRequestLeavesListenerSettingsUnspecified(string startup, bool confirmed)
    {
        var arguments = new List<string> { "mcp", "service", "configure", "--startup", startup, "--json" };
        if (confirmed) arguments.Add("--yes");
        var (result, requests) = await InvokeWithDaemonAsync(new HttpServiceStatus { AuthenticationMode = HttpAuthenticationModes.None },
            arguments.ToArray());
        ReadStatus(result);
        var configure = Assert.Single(requests, request => request.Method == DaemonProtocol.ConfigureHttpService);
        var sent = JsonSerializer.Deserialize<ConfigureHttpServiceRequest>(configure.PayloadJson, WebJson)!;

        Assert.Equal(startup, sent.StartupMode);
        Assert.Equal(confirmed, sent.Confirm);
        Assert.Null(sent.AuthenticationMode);
        Assert.Null(sent.BindMode);
        Assert.Null(sent.BindAddresses);
        Assert.Null(sent.Port);
    }

    [Theory]
    [InlineData(ErrorCodes.Conflict, ExitCodes.Rejected)]
    [InlineData(ErrorCodes.ConfirmationRequired, ExitCodes.Rejected)]
    [InlineData(ErrorCodes.InvalidRequest, ExitCodes.Usage)]
    [InlineData(ErrorCodes.Unavailable, ExitCodes.Unavailable)]
    [InlineData(ErrorCodes.Timeout, ExitCodes.Timeout)]
    public async Task ConfigurePreservesDaemonErrorsWithoutOfflineFallback(string errorCode, int exitCode)
    {
        var (result, _) = await InvokeWithDaemonAsync(new HttpServiceStatus(),
            ["mcp", "service", "configure", "--port", "9123", "--json"], errorCode: errorCode);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        using var json = JsonDocument.Parse(result.StandardError);
        Assert.Equal(errorCode, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(new ConfigStore(_directory).ConfigPath));
    }

    [Theory]
    [InlineData("managed-http-v1", "configure")]
    [InlineData("managed-http-v2", "configure")]
    [InlineData("managed-http-v1", "enable")]
    [InlineData("managed-http-v2", "enable")]
    [InlineData("managed-http-v1", "disable")]
    [InlineData("managed-http-v2", "disable")]
    public async Task OlderDaemonCapabilityRejectsAdministrationAndRequestsRestart(string capability, string operation)
    {
        var store = new ConfigStore(_directory);
        store.GetOrCreate();
        var before = File.ReadAllText(store.ConfigPath);
        string[] arguments = operation == "configure"
            ? ["mcp", "service", "configure", "--port", "9123", "--yes", "--json"]
            : ["mcp", "service", operation, "--yes", "--json"];
        var (result, requests) = await InvokeWithDaemonAsync(new HttpServiceStatus(), arguments, capability: capability);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(ErrorCodes.ProtocolMismatch, result.StandardError, StringComparison.Ordinal);
        Assert.Contains("restart", result.StandardError, StringComparison.Ordinal);
        Assert.All(requests, request => Assert.Equal(DaemonProtocol.Status, request.Method));
        Assert.True(before == File.ReadAllText(store.ConfigPath), "Unsupported peers must not trigger offline writes.");
    }

    [Fact]
    public async Task ConfigureHonorsCallerCancellationBeforeOfflineAccess()
    {
        var store = new ConfigStore(_directory);
        var daemon = new DaemonClient("unused.exe", "cancelled-settings-" + Guid.NewGuid().ToString("N"),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpAdminClient(daemon, store)
            .ConfigureServiceAsync(new ConfigureHttpServiceRequest { StartupMode = HttpStartupModes.Disabled }, cancellation.Token));
        Assert.False(File.Exists(store.ConfigPath));
    }

    [Fact]
    public async Task DisableStillRequiresConfirmationForActiveConnections()
    {
        var state = new HttpServiceStatus { Enabled = true, Running = true, ActiveConnectionCount = 1 };
        var (rejected, requests) = await InvokeWithDaemonAsync(state, ["mcp", "service", "disable", "--json"]);
        Assert.Equal(ExitCodes.Rejected, rejected.ExitCode);
        Assert.All(requests, request => Assert.Equal(DaemonProtocol.Status, request.Method));

        var (accepted, confirmedRequests) = await InvokeWithDaemonAsync(state, ["mcp", "service", "disable", "--yes", "--json"]);
        ReadStatus(accepted);
        var disable = Assert.Single(confirmedRequests, request => request.Method == DaemonProtocol.DisableHttpService);
        Assert.True(JsonSerializer.Deserialize<SetHttpServiceEnabledRequest>(disable.PayloadJson, WebJson)!.Confirm);
    }

    private static HttpServiceStatus ReadStatus(InvocationResult result)
    {
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Empty(result.StandardError);
        return JsonSerializer.Deserialize<HttpServiceStatus>(result.StandardOutput, WebJson)!;
    }

    /// <summary>Runs one CLI command against an isolated fake peer, recording only administrative request DTOs.</summary>
    /// <param name="state">The listener snapshot returned by discovery and administration requests.</param>
    /// <param name="arguments">Command arguments, including the requested output format.</param>
    /// <param name="capability">The managed HTTP capability advertised during discovery.</param>
    /// <param name="errorCode">An optional administrative failure returned after successful discovery.</param>
    private async Task<(InvocationResult Result, List<DaemonRequest> Requests)> InvokeWithDaemonAsync(
        HttpServiceStatus state, string[] arguments, string capability = "managed-http-v3",
        string? errorCode = null)
    {
        var pipeName = "fiddler-classic-cli.cli-settings-tests." + Guid.NewGuid().ToString("N");
        var (root, _) = CreateRoot(pipeName);
        var requests = new List<DaemonRequest>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(10));
        var peer = ServeAsync();
        try
        {
            var result = await Task.Run(() => Invoke(root, arguments), lifetime.Token);
            return (result, requests);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await peer; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        async Task ServeAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(lifetime.Token);
                var request = JsonSerializer.Deserialize<DaemonRequest>((await FrameCodec.ReadAsync(server, lifetime.Token))!, WebJson)!;
                requests.Add(request);
                object payload = request.Method == DaemonProtocol.Status
                    ? new DaemonStatus { Running = true, Capabilities = [capability], HttpService = state }
                    : state;
                var success = errorCode is null || request.Method == DaemonProtocol.Status;
                await FrameCodec.WriteAsync(server, JsonSerializer.Serialize(new DaemonResponse
                {
                    RequestId = request.RequestId,
                    Success = success,
                    Error = success ? null : new BridgeError { Code = errorCode!, Message = "Synthetic administrative failure." },
                    PayloadJson = JsonSerializer.Serialize(payload, WebJson)
                }, WebJson), lifetime.Token);
            }
        }
    }
}
