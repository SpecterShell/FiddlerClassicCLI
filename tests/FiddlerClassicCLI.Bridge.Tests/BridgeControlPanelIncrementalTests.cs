// Verifies no-op snapshots, changed-cell updates, sorting, and stable selection with synthetic metadata.
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PanelSnapshotsRetainRowsAndOnlyChangeDifferingCells(bool connections) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient
        {
            Clients = new ListHttpClientsResponse { Clients = new List<AuthorizedHttpClientDto>
            {
                new AuthorizedHttpClientDto { ClientId = "first", Name = "Test client" }
            } },
            Connections = new ListHttpConnectionsResponse { Connections = new List<HttpConnectionDto>
            {
                new HttpConnectionDto { ConnectionId = "first", RemoteEndpoint = "127.0.0.1:1234" }
            } }
        };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        var grid = Named<StatusGrid>(panel, connections ? "Active connections" : "Authorized clients");
        var row = grid.Rows[0];
        grid.CurrentCell = row.Cells[1];
        row.Selected = true;
        var cells = 0;
        var membership = 0;
        var selections = 0;
        grid.CellValueChanged += (_, _) => cells++;
        grid.RowsAdded += (_, _) => membership++;
        grid.RowsRemoved += (_, _) => membership++;
        grid.SelectionChanged += (_, _) => selections++;
        CompleteRefresh(panel);
        Assert.Equal(0, cells);
        Assert.Equal(0, membership);
        Assert.Equal(0, selections);
        if (connections) client.Connections.Connections[0].TotalRequestCount = 7;
        else client.Clients.Clients[0].ActiveConnectionCount = 7;
        CompleteRefresh(panel);
        Assert.Equal(1, cells);
        Assert.Equal(0, membership);
        Assert.Equal(0, selections);
        Assert.Same(row, grid.Rows[0]);
        Assert.True(row.Selected);
    });

    [Fact]
    public void SortChangesKeepRowIdentityAndViewportAndRemoveOnlyMissingRows() => RunOnSta(() =>
    {
        using var grid = TestStatusGrid();
        var snapshot = Enumerable.Range(0, 30).Select(index => new object[] { $"id-{index}", index }).ToArray();
        grid.UpdateRows(snapshot);
        grid.Sort(grid.Columns[1], ListSortDirection.Descending);
        var selected = grid.Rows.Cast<DataGridViewRow>().Single(row => (string)row.Tag == "id-15");
        grid.CurrentCell = selected.Cells[1];
        selected.Selected = true;
        grid.FirstDisplayedScrollingRowIndex = 10;
        grid.HorizontalScrollingOffset = 20;
        var horizontal = grid.HorizontalScrollingOffset;
        var top = grid.Rows[10];
        snapshot[15][1] = 99;
        grid.UpdateRows(snapshot.AsEnumerable().Reverse());
        Assert.Same(selected, grid.Rows[0]);
        Assert.Same(selected, grid.SelectedRows[0]);
        Assert.Same(top, grid.Rows[grid.FirstDisplayedScrollingRowIndex]);
        Assert.Equal(horizontal, grid.HorizontalScrollingOffset);
        Assert.Equal(SortOrder.Descending, grid.SortOrder);
        var stable = grid.Rows.Cast<DataGridViewRow>().Single(row => (string)row.Tag == "id-14");
        grid.UpdateRows(snapshot.Where(values => (string)values[0] != "id-15").Concat(new[] { new object[] { "new", 100 } }));
        Assert.Equal("new", grid.Rows[0].Tag);
        Assert.Contains(stable, grid.Rows.Cast<DataGridViewRow>());
        Assert.Null(grid.CurrentCell);
        Assert.Empty(grid.SelectedRows.Cast<DataGridViewRow>());
    });

    [Fact]
    public void InvalidSnapshotsLeaveTheExistingRowsUntouched() => RunOnSta(() =>
    {
        using var grid = TestStatusGrid();
        grid.UpdateRows(new[] { new object[] { "first", 1 } });
        var row = grid.Rows[0];
        foreach (var invalid in new[]
        {
            new[] { new object[] { "first", 2 }, new object[] { "first", 3 } },
            new[] { new object[] { "", 2 } },
            new[] { new object[] { "first" } }
        })
        {
            Assert.Throws<ArgumentException>(() => grid.UpdateRows(invalid));
            Assert.Same(row, grid.Rows[0]);
            Assert.Equal(1, grid.Rows[0].Cells[1].Value);
        }
    });

    private static StatusGrid TestStatusGrid()
    {
        var grid = new StatusGrid { Size = new Size(250, 150), AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
        grid.Columns.Add("id", "ID");
        grid.Columns.Add("count", "Count");
        foreach (DataGridViewColumn column in grid.Columns) column.Width = 180;
        grid.CreateControl();
        return grid;
    }
}
