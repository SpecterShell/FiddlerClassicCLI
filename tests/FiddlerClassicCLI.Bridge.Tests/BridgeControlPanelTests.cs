// Verifies the management panel can be created and disposed on a WinForms STA thread.
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Fact]
    public void ConstructsAndDisposesOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new BridgeControlPanel(new FakeHostControlClient());
                Assert.Single(panel.Controls);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The WinForms smoke-test thread did not finish.");
        Assert.Null(failure);
    }

    [Fact]
    public void RefreshesStateAndReportsErrorsOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var client = new FakeHostControlClient
                {
                    Service = new HttpServiceStatus
                    {
                        Enabled = true,
                        Running = true,
                        BindAddress = "127.0.0.1",
                        Port = 9001
                    },
                    Clients = new ListHttpClientsResponse
                    {
                        Clients = new List<AuthorizedHttpClientDto> { new AuthorizedHttpClientDto { ClientId = "default" } }
                    },
                    Connections = new ListHttpConnectionsResponse
                    {
                        Connections = new List<HttpConnectionDto> { new HttpConnectionDto { ConnectionId = "connection-1" } }
                    }
                };
                using var panel = new BridgeControlPanel(client);

                PumpUntilCompleted(panel.RefreshForTestingAsync());

                Assert.Equal("Listening on 127.0.0.1:9001", panel.ServiceStateText);
                Assert.True(panel.ServiceSettingsEnabled);
                Assert.Equal(1, panel.ClientRowCount);
                Assert.Equal(1, panel.ConnectionRowCount);

                client.Failure = new InvalidOperationException("version mismatch");
                PumpUntilCompleted(panel.RefreshForTestingAsync());
                Assert.Equal("version mismatch", panel.ServiceErrorText);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The WinForms state-test thread did not finish.");
        Assert.Null(failure);
    }

    [Fact]
    public void ReportsMissingManagedHttpCapability()
    {
        var capability = Assert.Throws<InvalidOperationException>(() =>
            HostControlClient.EnsureCapability(new DaemonStatus()));

        Assert.Contains("does not support the current MCP HTTP settings", capability.Message);
        Assert.Contains("Install the matching CLI and bridge", capability.Message);
        Assert.Contains("daemon stop", capability.Message);
    }

    [Fact]
    public void CancelsAnInFlightRefreshWhenDisposed()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var client = new FakeHostControlClient { WaitForCancellation = true };
                var panel = new BridgeControlPanel(client);
                var refresh = panel.RefreshForTestingAsync();

                panel.Dispose();
                PumpUntilCompleted(refresh);

                Assert.Equal(TaskStatus.RanToCompletion, refresh.Status);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The WinForms cancellation-test thread did not finish.");
        Assert.Null(failure);
    }

    private sealed class FakeHostControlClient : IHostControlClient
    {
        public string PipeName { get; set; } = "test-daemon-pipe";
        public HttpServiceStatus Service { get; set; } = new HttpServiceStatus();
        public ListHttpClientsResponse Clients { get; set; } = new ListHttpClientsResponse();
        public ListHttpConnectionsResponse Connections { get; set; } = new ListHttpConnectionsResponse();
        public Exception? Failure { get; set; }
        public Exception? DaemonFailure { get; set; }
        public Exception? ServiceFailure { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource<HttpServiceStatus>? PendingService { get; set; }
        public DaemonStatus Daemon { get; set; } = new DaemonStatus { HostVersion = "test-v1" };
        public HostLaunchRecord? LaunchRecord { get; set; }
        public int StartCount { get; private set; }
        public int StartupCount { get; private set; }
        public TaskCompletionSource<HostStartResult>? PendingStart { get; set; }
        public int CredentialActionCount { get; private set; }
        public ConfigureHttpServiceRequest? LastConfiguration { get; private set; }
        public bool? LastConfirmation { get; private set; }
        public Exception? MutationFailure { get; set; }
        public Action? BeforeMutation { get; set; }
        public int ServiceStatusCount { get; private set; }
        public int DaemonStatusCount { get; private set; }
        public int ConfigureCount { get; private set; }
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public List<string> MutationCalls { get; } = new List<string>();

        public HostLaunchRecord? ReadLaunchRecord() => LaunchRecord;

        public Task<HostStartResult> EnsureStartedAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return PendingStart?.Task ?? Complete(HostStartResult.Reused, cancellationToken);
        }

        public Task<DaemonStatus> GetDaemonStatusAsync(CancellationToken cancellationToken)
        {
            DaemonStatusCount++;
            return DaemonFailure is null ? Complete(Daemon, cancellationToken) : Task.FromException<DaemonStatus>(DaemonFailure);
        }

        public Task<HttpServiceStatus> GetServiceStatusAsync(CancellationToken cancellationToken)
        {
            ServiceStatusCount++;
            return ServiceFailure is null ? PendingService?.Task ?? Complete(Service, cancellationToken)
                : Task.FromException<HttpServiceStatus>(ServiceFailure);
        }

        public Task<HttpServiceStatus> ConfigureServiceAsync(
            ConfigureHttpServiceRequest request,
            CancellationToken cancellationToken)
        {
            ConfigureCount++;
            MutationCalls.Add("configure");
            LastConfiguration = request;
            BeforeMutation?.Invoke();
            if (MutationFailure is not null) return Task.FromException<HttpServiceStatus>(MutationFailure);
            if ((Service.Enabled || Service.Running) && (request.BindMode is not null || request.Port.HasValue
                || request.BindAddresses is not null || request.AuthenticationMode is not null))
                return Task.FromException<HttpServiceStatus>(new InvalidOperationException("Disable MCP HTTP before configuring bindings or authentication."));
            var next = CloneService();
            if (request.BindMode is not null) next.BindMode = request.BindMode;
            if (request.Port.HasValue) next.Port = request.Port.Value;
            if (request.BindAddresses is not null) next.BindAddresses = request.BindAddresses;
            if (request.StartupMode is not null) next.StartupMode = request.StartupMode;
            if (request.AuthenticationMode is not null) next.AuthenticationMode = request.AuthenticationMode;
            if ((request.AuthenticationMode == HttpAuthenticationModes.None || next.StartupMode == HttpStartupModes.Enabled && RiskyAccess(next)) && !request.Confirm)
                return Task.FromException<HttpServiceStatus>(new InvalidOperationException("confirmation_required"));
            Service = next;
            return Task.FromResult(Service);
        }

        public Task<HttpServiceStatus> ApplyStartupAsync(CancellationToken cancellationToken)
        {
            StartupCount++;
            if (Service.StartupMode == HttpStartupModes.Enabled) Service.Enabled = true;
            else if (Service.StartupMode == HttpStartupModes.Disabled) Service.Enabled = false;
            return Complete(Service, cancellationToken);
        }

        public Task<HttpServiceStatus> EnableServiceAsync(bool confirm, CancellationToken cancellationToken)
        {
            EnableCount++;
            MutationCalls.Add("enable");
            LastConfirmation = confirm;
            BeforeMutation?.Invoke();
            if (MutationFailure is not null) return Task.FromException<HttpServiceStatus>(MutationFailure);
            if (RiskyAccess(Service) && !confirm)
                return Task.FromException<HttpServiceStatus>(new InvalidOperationException("confirmation_required"));
            Service = CloneService();
            Service.Enabled = true;
            return Task.FromResult(Service);
        }

        public Task<HttpServiceStatus> DisableServiceAsync(bool confirm, CancellationToken cancellationToken)
        {
            DisableCount++;
            MutationCalls.Add("disable");
            LastConfirmation = confirm;
            BeforeMutation?.Invoke();
            if (MutationFailure is not null) return Task.FromException<HttpServiceStatus>(MutationFailure);
            if (Service.ActiveConnectionCount > 0 && !confirm)
                return Task.FromException<HttpServiceStatus>(new InvalidOperationException("confirmation_required"));
            Service = CloneService();
            Service.Enabled = false;
            Service.Running = false;
            return Task.FromResult(Service);
        }

        public Task<ListHttpClientsResponse> ListClientsAsync(CancellationToken cancellationToken) =>
            Complete(Clients, cancellationToken);

        public Task<AuthorizeHttpClientResponse> AuthorizeClientAsync(
            string name,
            CancellationToken cancellationToken)
        {
            CredentialActionCount++;
            return Task.FromResult(new AuthorizeHttpClientResponse());
        }

        public Task<DeauthorizeHttpClientResponse> DeauthorizeClientAsync(
            string clientId,
            CancellationToken cancellationToken)
        {
            CredentialActionCount++;
            return Task.FromResult(new DeauthorizeHttpClientResponse());
        }

        private HttpServiceStatus CloneService()
        {
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            return serializer.Deserialize<HttpServiceStatus>(serializer.Serialize(Service));
        }

        private static bool RiskyAccess(HttpServiceStatus service) => service.AuthenticationMode == HttpAuthenticationModes.None
            || service.BindMode == HttpBindModes.All
            || service.BindMode == HttpBindModes.Selected && service.BindAddresses.Any(value =>
                !System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(value)));

        public Task<ListHttpConnectionsResponse> ListConnectionsAsync(CancellationToken cancellationToken) =>
            Complete(Connections, cancellationToken);

        public Task<DisconnectHttpConnectionResponse> DisconnectAsync(
            string connectionId,
            CancellationToken cancellationToken) => Task.FromResult(new DisconnectHttpConnectionResponse());

        private Task<T> Complete<T>(T value, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException<T>(Failure);
            }

            if (!WaitForCancellation)
            {
                return Task.FromResult(value);
            }

            return WaitForCancellationAsync<T>(cancellationToken);
        }

        private static async Task<T> WaitForCancellationAsync<T>(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The cancellation wait unexpectedly completed.");
        }
    }
}
