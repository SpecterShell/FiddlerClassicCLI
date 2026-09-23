// Builds startup, authentication, and fixed project links in the native Settings subtab.
using System.Diagnostics;
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel
{
    private GroupBox CreatePreferencesGroup()
    {
        _startup.Items.AddRange(new object[] { HttpStartupModes.Enabled, HttpStartupModes.Disabled, HttpStartupModes.LastState });
        _startup.SelectedItem = HttpStartupModes.LastState;
        _startup.FitChoices();
        _authenticationMode.Items.AddRange(new object[]
        {
            new AuthenticationChoice(HttpAuthenticationModes.Required, "Require for all"),
            new AuthenticationChoice(HttpAuthenticationModes.NonLoopback, "Non-loopback only"),
            new AuthenticationChoice(HttpAuthenticationModes.None, "No authentication")
        });
        _authenticationMode.SelectedIndex = 1;
        _authenticationMode.FitChoices();
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, "&Startup", _startup);
        AddFullRow(layout, new Label
        {
            AutoSize = true, UseMnemonic = false, AccessibleName = "Startup instructions",
            Text = "Choose whether MCP HTTP starts enabled, disabled, or in its last saved state. " +
                "This applies when Fiddler loads and when the daemon starts."
        });
        var authenticationRow = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Margin = Padding.Empty, TabStop = false
        };
        authenticationRow.Controls.Add(new Label
        {
            Text = "&Authentication", AccessibleName = "Authentication", AutoSize = true,
            Anchor = AnchorStyles.Left, Margin = PanelStyle.CaptionMargin, TabIndex = 0
        });
        _authenticationMode.Anchor = AnchorStyles.Left;
        _authenticationMode.Margin = PanelStyle.ValueMargin;
        _authenticationMode.TabIndex = 1;
        authenticationRow.Controls.Add(_authenticationMode);
        AddFullRow(layout, authenticationRow);
        AddFullRow(layout, new Label
        {
            AutoSize = true, UseMnemonic = false, AccessibleName = "Authentication instructions",
            Text = "Non-loopback only is the default. Connections using loopback addresses at both ends do not require a bearer token. " +
                "Other connections must authenticate. Require for all also authenticates loopback connections. " +
                "No authentication gives every reachable client full MCP access. " +
                "Disable MCP HTTP before changing this setting."
        });
        AddFullRow(layout, PanelStyle.CreateActions(_applyPreferences));
        return new ContentGroupBox("MCP settings", layout) { AccessibleName = "MCP settings", TabStop = false };
    }

    private string? SelectedAuthenticationMode => (_authenticationMode.SelectedItem as AuthenticationChoice)?.Mode;

    private sealed class AuthenticationChoice
    {
        internal AuthenticationChoice(string mode, string label) { Mode = mode; _label = label; }
        internal string Mode { get; }
        private readonly string _label;
        public override string ToString() => _label;
    }

    private GroupBox CreateDocumentationGroup()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddFullRow(layout, CreateLink("Project", "https://github.com/SpecterShell/FiddlerClassicCLI"));
        AddFullRow(layout, CreateLink("Documentation", "https://github.com/SpecterShell/FiddlerClassicCLI/tree/main/docs/en-US"));
        return new ContentGroupBox("Documentation", layout) { AccessibleName = "Documentation", TabStop = false };
    }

    private LinkLabel CreateLink(string title, string url)
    {
        var link = new LinkLabel { Text = title, AccessibleName = title + " link", AccessibleDescription = url, AutoSize = true, TabStop = true };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception exception) { _showError("Could not open the link: " + exception.Message); }
        };
        return link;
    }
}
