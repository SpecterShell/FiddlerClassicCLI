// Checks that periodic snapshots preserve pending edits and stable destructive-action targets.
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Fact]
    public void PreservesEditsUntilAppliedAndThenFollowsSavedSettings() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var bind = Descendants(panel).OfType<ComboBox>().Single();
        var port = Descendants(panel).OfType<NumericUpDown>().Single();
        bind.SelectedItem = HttpBindModes.All;
        port.Value = 9002;

        client.Service = new HttpServiceStatus { Port = 9003 };
        CompleteRefresh(panel);
        CompleteRefresh(panel);
        Assert.Equal(HttpBindModes.All, bind.SelectedItem);
        Assert.Equal(9002, port.Value);
        Assert.False(Button(panel, "Enable").Enabled);

        Button(panel, "Apply").PerformClick();
        PumpUntilIdle(panel);
        Assert.Equal(9002, client.Service.Port);
        Assert.Equal(HttpBindModes.All, client.Service.BindMode);
        Assert.True(Button(panel, "Enable").Enabled);
        client.Service = new HttpServiceStatus { Port = 9010 };
        CompleteRefresh(panel);
        Assert.Equal(9010, port.Value);
        Assert.Equal(HttpBindModes.Loopback, bind.SelectedItem);
    });

    [Fact]
    public void KeepsEditsMadeDuringAnInFlightRefreshAndRecoversAfterErrors() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var port = Descendants(panel).OfType<NumericUpDown>().Single();
        client.PendingService = new TaskCompletionSource<HttpServiceStatus>();
        var refresh = panel.RefreshForTestingAsync();
        port.Text = "9200";
        client.PendingService.SetResult(new HttpServiceStatus { Port = 9000 });
        PumpUntilCompleted(refresh);
        Assert.Equal("9200", port.Text);
        Assert.Equal(9200, port.Value);

        client.PendingService = null;
        client.Failure = new TimeoutException("daemon timeout");
        CompleteRefresh(panel);
        Assert.Equal("daemon timeout", panel.ServiceErrorText);
        Assert.True(Button(panel, "Refresh").Enabled);
        Assert.Equal(9200, port.Value);
        client.Failure = null;
        CompleteRefresh(panel);
        Assert.Equal(9200, port.Value);
        Assert.Empty(panel.ServiceErrorText);
    });

    [Theory]
    [InlineData("clientId", "Deauthorize")]
    [InlineData("connectionId", "Disconnect")]
    public void PreservesGridSelectionAndViewportAndClearsVanishedTargets(string column, string action) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient
        {
            Clients = new ListHttpClientsResponse
            {
                Clients = Enumerable.Range(0, 30).Select(index => new AuthorizedHttpClientDto { ClientId = "id-" + index }).ToList()
            },
            Connections = new ListHttpConnectionsResponse
            {
                Connections = Enumerable.Range(0, 30).Select(index => new HttpConnectionDto { ConnectionId = "id-" + index }).ToList()
            }
        };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var grid = Descendants(panel).OfType<DataGridView>().Single(candidate => candidate.Columns.Contains(column));
        grid.CreateControl();
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.ClearSelection();
        grid.CurrentCell = grid.Rows[12].Cells[1];
        grid.Rows[12].Selected = true;
        grid.FirstDisplayedScrollingRowIndex = 10;
        grid.HorizontalScrollingOffset = 20;
        var horizontalOffset = grid.HorizontalScrollingOffset;
        var topId = grid.Rows[grid.FirstDisplayedScrollingRowIndex].Tag;
        Assert.True(Button(panel, action).Enabled);

        client.Clients.Clients.Reverse();
        client.Connections.Connections.Reverse();
        CompleteRefresh(panel);
        Assert.Equal("id-12", Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>()).Tag);
        Assert.Equal(topId, grid.Rows[grid.FirstDisplayedScrollingRowIndex].Tag);
        Assert.Equal(horizontalOffset, grid.HorizontalScrollingOffset);
        Assert.Equal(1, grid.CurrentCell.ColumnIndex);

        client.Clients.Clients.RemoveAll(item => item.ClientId == "id-12");
        client.Connections.Connections.RemoveAll(item => item.ConnectionId == "id-12");
        CompleteRefresh(panel);
        Assert.Empty(grid.SelectedRows.Cast<DataGridViewRow>());
        Assert.Null(grid.CurrentCell);
        Assert.False(Button(panel, action).Enabled);
        CompleteRefresh(panel);
        Assert.Empty(grid.SelectedRows.Cast<DataGridViewRow>());
    });

    [Fact]
    public void AdoptsRunningSettingsWhenAnotherClientEnablesTheService() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var port = Descendants(panel).OfType<NumericUpDown>().Single();
        port.Value = 9500;
        client.Service = new HttpServiceStatus { Enabled = true, Running = true, Port = 9000 };
        CompleteRefresh(panel);
        Assert.Equal(9000, port.Value);
        Assert.False(panel.ServiceSettingsEnabled);
        Assert.True(Button(panel, "Disable").Enabled);
    });

    [Fact]
    public void IgnoresLateRefreshCompletionAfterDisposal() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { PendingService = new TaskCompletionSource<HttpServiceStatus>() };
        var panel = new BridgeControlPanel(client);
        var refresh = panel.RefreshForTestingAsync();
        panel.Dispose();
        client.PendingService.SetResult(new HttpServiceStatus());
        PumpUntilCompleted(refresh);
        Assert.Equal(TaskStatus.RanToCompletion, refresh.Status);
    });

    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>()
        .SelectMany(control => new[] { control }.Concat(Descendants(control)));

    private static Button Button(Control parent, string text) => Descendants(parent).OfType<Button>().Single(button => button.Text.Replace("&", string.Empty) == text);

    private static void CompleteRefresh(BridgeControlPanel panel) => PumpUntilCompleted(panel.RefreshForTestingAsync());

    private static void PumpUntilIdle(BridgeControlPanel panel)
    {
        var context = SynchronizationContext.Current;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (panel.IsBusy && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        SynchronizationContext.SetSynchronizationContext(context);
        Assert.False(panel.IsBusy);
    }

    private static void PumpUntilCompleted(Task task)
    {
        var context = SynchronizationContext.Current;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        // DoEvents tears down its temporary message loop and may clear the installed context.
        SynchronizationContext.SetSynchronizationContext(context);
        Assert.True(task.IsCompleted, "The UI operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static void RunOnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // These headless tests pump messages without Application.Run. Install a stable UI
            // context explicitly so later refresh continuations never create controls on a worker.
            using var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try { test(); }
            catch (Exception exception) { failure = exception; }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The WinForms regression test did not finish.");
        Assert.Null(failure);
    }
}
