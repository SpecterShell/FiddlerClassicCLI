// Builds the width-constrained management sections, aligned service rows, and wrapping actions.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel
{
    private void BuildLayout()
    {
        _bindMode.Items.AddRange(new object[] { HttpBindModes.Loopback, HttpBindModes.All, HttpBindModes.Selected });
        _bindMode.SelectedIndex = 0;
        _bindMode.FitChoices();

        var tabs = new TabControl { Dock = DockStyle.Fill, Multiline = true, AccessibleName = "Management sections", TabIndex = 0 };
        tabs.TabPages.Add(CreatePage("MCP", CreateServiceLayout(), CreateAddressesGroup(), CreateClientsGroup(), CreateConnectionsGroup()));
        tabs.TabPages.Add(CreatePage("Named pipes", CreatePipesLayout()));
        tabs.TabPages.Add(CreatePage("Settings", CreatePreferencesGroup(), CreateVersionsGroup(), CreateDocumentationGroup()));
        Controls.Add(tabs);
    }

    /// <summary>Gives each native subtab its own vertically scrollable, width-constrained content.</summary>
    /// <param name="title">The visible and accessible tab name.</param>
    /// <param name="sections">Sections in visual and keyboard order.</param>
    private static TabPage CreatePage(string title, params Control[] sections)
    {
        var page = new TabPage(title) { AccessibleName = title, AutoScroll = true, UseVisualStyleBackColor = true };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = sections.Length,
            Padding = PanelStyle.PagePadding,
            TabStop = false,
            TabIndex = 0
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < sections.Length; index++)
        {
            sections[index].TabIndex = index;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(sections[index], 0, index);
        }
        page.Controls.Add(root);
        return page;
    }

    private TableLayoutPanel CreateServiceLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, AccessibleName = "MCP service controls" };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddFullRow(layout, _enabled);
        AddRow(layout, "&Bind", _bindMode);
        AddFullRow(layout, _interfaces);
        AddRow(layout, "&Port", _port);
        AddFullRow(layout, PanelStyle.CreateActions(_apply, _refresh));
        AddMessageRow(layout, _authenticationWarning);
        AddMessageRow(layout, _serviceError);
        return layout;
    }

    private GroupBox CreateAddressesGroup()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var endpointRow = new EndpointRow(_endpoint, _copyEndpoint);
        AddRow(layout, "Loopback", endpointRow);
        var endpointRowIndex = layout.RowCount - 1;
        var endpointCaption = layout.GetControlFromPosition(0, endpointRowIndex)!;
        endpointCaption.Visible = endpointRow.Visible = false;
        _endpoint.TextChanged += (_, _) => endpointCaption.Visible = endpointRow.Visible = _endpoint.Text.Length > 0;
        // Reserve a collapsed row for the address when the caption leaves too little text space.
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Layout += (_, _) => FitEndpointRow(layout, endpointCaption, endpointRow, endpointRowIndex);

        AddFullRow(layout, _lanEndpoints);
        return new ContentGroupBox("MCP addresses", layout) { AccessibleName = "MCP addresses", TabStop = false };
    }

    /// <summary>Moves the URL below its caption when a narrow pane cannot fit readable text beside the copy button.</summary>
    /// <param name="layout">The two-column service layout, with an empty row reserved below the caption.</param>
    /// <param name="caption">The URL caption whose natural width contributes to the breakpoint.</param>
    /// <param name="endpoint">The selectable address and adjacent clipboard action.</param>
    /// <param name="row">The caption's fixed row index.</param>
    private void FitEndpointRow(TableLayoutPanel layout, Control caption, EndpointRow endpoint, int row)
    {
        var minimumText = TextRenderer.MeasureText("MMMMMM", _endpoint.Font).Width;
        var required = caption.GetPreferredSize(Size.Empty).Width + caption.Margin.Horizontal
            + minimumText + _copyEndpoint.GetPreferredSizeForText(string.Empty).Width
            + _endpoint.Margin.Horizontal + _copyEndpoint.Margin.Horizontal + endpoint.Margin.Horizontal;
        var stacked = layout.ClientSize.Width < required;
        var span = stacked ? 2 : 1;
        if (layout.GetColumnSpan(endpoint) == span) return;
        layout.SuspendLayout();
        try
        {
            layout.SetColumnSpan(endpoint, span);
            layout.SetCellPosition(endpoint, new TableLayoutPanelCellPosition(stacked ? 0 : 1, stacked ? row + 1 : row));
        }
        finally { layout.ResumeLayout(performLayout: true); }
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
        var actions = PanelStyle.CreateActions(buttons);
        actions.TabIndex = 1;
        layout.Controls.Add(actions, 0, 1);
        return new ContentGroupBox(title, layout) { AccessibleName = title, TabStop = false };
    }

    private GroupBox CreateVersionsGroup()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddFullRow(layout, _versions);
        return new ContentGroupBox("Versions", layout) { AccessibleName = "Versions", TabStop = false };
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
            Margin = PanelStyle.CaptionMargin
        }, 0, row);
        control.Anchor = AnchorStyles.Left;
        control.TabIndex = row * 2 + 1;
        control.Margin = PanelStyle.ValueMargin;
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

    private static void AddMessageRow(TableLayoutPanel layout, Label message)
    {
        message.Visible = message.Text.Length > 0;
        message.TextChanged += (_, _) => message.Visible = message.Text.Length > 0;
        AddFullRow(layout, message);
    }
}
