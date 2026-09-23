// Builds the width-constrained management sections, aligned service rows, and wrapping actions.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel
{
    private void BuildLayout()
    {
        _bindMode.Items.AddRange(new object[] { HttpBindModes.Loopback, HttpBindModes.All });
        _bindMode.SelectedIndex = 0;
        _bindMode.FitChoices();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8),
            TabStop = false,
            TabIndex = 0
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(CreateServiceGroup(), 0, 0);
        root.Controls.Add(CreateClientsGroup(), 0, 1);
        root.Controls.Add(CreateConnectionsGroup(), 0, 2);
        root.Controls.Add(CreatePipesGroup(), 0, 3);
        root.Controls.Add(CreateVersionsGroup(), 0, 4);
        for (var index = 0; index < root.Controls.Count; index++)
            root.Controls[index].TabIndex = index;
        Controls.Add(root);
    }

    private GroupBox CreateServiceGroup()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var status = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, TabStop = false };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _serviceIndicator.Anchor = AnchorStyles.Left;
        _serviceState.Dock = DockStyle.Fill;
        _serviceState.TextAlign = ContentAlignment.MiddleLeft;
        status.Controls.Add(_serviceIndicator, 0, 0);
        status.Controls.Add(_serviceState, 1, 0);
        AddRow(layout, "Status", status);
        AddRow(layout, "Loopback", new EndpointRow(_endpoint, _copyEndpoint));

        AddRow(layout, "&Bind", _bindMode);
        AddRow(layout, "&Port", _port);
        AddFullRow(layout, CreateActions(_apply, _toggle, _refresh));
        AddFullRow(layout, _lanEndpoints);
        AddFullRow(layout, _serviceError);
        return new ContentGroupBox("MCP HTTP service", layout) { AccessibleName = "MCP HTTP service", TabStop = false };
    }

    private GroupBox CreateClientsGroup()
    {
        _clients.Columns.Add("clientId", "ID");
        _clients.Columns.Add("name", "Name");
        _clients.Columns.Add("created", "Created (UTC)");
        _clients.Columns.Add("lastSeen", "Last seen (UTC)");
        _clients.Columns.Add("active", "Active");
        return CreateGridGroup("Authorized clients", _clients, _authorize, _deauthorize, _rotateDefault);
    }

    private GroupBox CreateConnectionsGroup()
    {
        _connections.Columns.Add("connectionId", "Connection ID");
        _connections.Columns.Add("remote", "Remote endpoint");
        _connections.Columns.Add("state", "Authorization");
        _connections.Columns.Add("clients", "Clients");
        _connections.Columns.Add("connected", "Connected (UTC)");
        _connections.Columns.Add("lastActivity", "Last activity (UTC)");
        _connections.Columns.Add("requests", "Requests");
        _connections.Columns.Add("active", "Active");
        return CreateGridGroup("Active connections", _connections, _disconnect);
    }

    /// <summary>Lets wrapped action rows grow without taking height away from their grid.</summary>
    /// <param name="title">The accessible section heading.</param>
    /// <param name="grid">The read-only records grid owned by the panel.</param>
    /// <param name="buttons">Actions displayed below the grid in keyboard navigation order.</param>
    private static GroupBox CreateGridGroup(string title, DataGridView grid, params Button[] buttons)
    {
        grid.AccessibleName = title;
        grid.TabIndex = 0;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(grid, 0, 0);
        var actions = CreateActions(buttons);
        actions.TabIndex = 1;
        layout.Controls.Add(actions, 0, 1);
        return new ContentGroupBox(title, layout) { AccessibleName = title, TabStop = false };
    }

    private static FlowLayoutPanel CreateActions(params Button[] buttons)
    {
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = Padding.Empty,
            TabStop = false
        };
        for (var index = 0; index < buttons.Length; index++)
            buttons[index].TabIndex = index;
        actions.Controls.AddRange(buttons);
        return actions;
    }

    private GroupBox CreateVersionsGroup()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddFullRow(layout, _versions);
        return new ContentGroupBox("Versions", layout) { AccessibleName = "Versions", TabStop = false };
    }

    private static DataGridView CreateGrid()
    {
        return new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.False },
            RowHeadersVisible = false
        };
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = label,
            AccessibleName = label.Replace("&", string.Empty),
            TabIndex = row * 2,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(3, 4, 12, 4)
        }, 0, row);
        control.Anchor = AnchorStyles.Left;
        control.TabIndex = row * 2 + 1;
        control.Margin = new Padding(3, 4, 3, 4);
        if (control is Label value)
        {
            value.Dock = DockStyle.Fill;
            value.TextAlign = ContentAlignment.MiddleLeft;
        }
        else if (control is TableLayoutPanel || control is EndpointRow)
        {
            control.Dock = DockStyle.Fill;
        }
        layout.Controls.Add(control, 1, row);
    }

    private static void AddFullRow(TableLayoutPanel layout, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Fill;
        control.TabIndex = row * 2;
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, layout.ColumnCount);
    }
}
