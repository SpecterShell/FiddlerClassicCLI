// Verifies the management panel can be created and disposed on a WinForms STA thread.
using FiddlerClassic.Bridge;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge.Tests;

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
                Assert.False(panel.ServiceSettingsEnabled);
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

        Assert.Contains("predates managed MCP HTTP support", capability.Message);
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
        public HttpServiceStatus Service { get; set; } = new HttpServiceStatus();
        public ListHttpClientsResponse Clients { get; set; } = new ListHttpClientsResponse();
        public ListHttpConnectionsResponse Connections { get; set; } = new ListHttpConnectionsResponse();
        public Exception? Failure { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource<HttpServiceStatus>? PendingService { get; set; }
        public DaemonStatus Daemon { get; set; } = new DaemonStatus { HostVersion = "test-v1" };
        public HostLaunchRecord? LaunchRecord { get; set; }
        public int StartCount { get; private set; }

        public HostLaunchRecord? ReadLaunchRecord() => LaunchRecord;

        public Task<DaemonStatus> EnsureStartedAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Complete(Daemon, cancellationToken);
        }

        public Task<DaemonStatus> GetDaemonStatusAsync(CancellationToken cancellationToken) => Complete(Daemon, cancellationToken);

        public Task<HttpServiceStatus> GetServiceStatusAsync(CancellationToken cancellationToken) =>
            PendingService?.Task ?? Complete(Service, cancellationToken);

        public Task<HttpServiceStatus> ConfigureServiceAsync(
            string bindMode,
            int port,
            CancellationToken cancellationToken)
        {
            Service = new HttpServiceStatus { BindMode = bindMode, Port = port };
            return Task.FromResult(Service);
        }

        public Task<HttpServiceStatus> EnableServiceAsync(bool confirm, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpServiceStatus());

        public Task<HttpServiceStatus> DisableServiceAsync(bool confirm, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpServiceStatus());

        public Task<ListHttpClientsResponse> ListClientsAsync(CancellationToken cancellationToken) =>
            Complete(Clients, cancellationToken);

        public Task<AuthorizeHttpClientResponse> AuthorizeClientAsync(
            string name,
            CancellationToken cancellationToken) => Task.FromResult(new AuthorizeHttpClientResponse());

        public Task<DeauthorizeHttpClientResponse> DeauthorizeClientAsync(
            string clientId,
            CancellationToken cancellationToken) => Task.FromResult(new DeauthorizeHttpClientResponse());

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
