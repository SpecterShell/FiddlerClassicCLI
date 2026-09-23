// Exercises endpoint copies, accessible labels, keyboard order, and dialog sizing without live services.
using System.Drawing;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiesUseClientUrlsAndRemainAvailableDuringRefresh(bool enabled) => RunOnSta(() =>
    {
        var client = new FakeHostControlClient { Service = AllInterfaceStatus() };
        client.Service.Enabled = enabled;
        using var panel = new BridgeControlPanel(client);
        Assert.False(Named<Button>(panel, "Copy loopback endpoint").Enabled);
        CompleteRefresh(panel);
        Assert.Equal("http://127.0.0.1:9101/mcp", Named<SelectableAddress>(panel, "Loopback endpoint").Text);
        Assert.Equal("http://192.168.1.4:9101/mcp", Named<Button>(panel, "Copy LAN endpoint 1").AccessibleDescription);
        Assert.DoesNotContain(Descendants(panel).OfType<SelectableAddress>(), address => address.Text.Contains("http://0.0.0.0"));
        var lanCopy = Named<Button>(panel, "Copy LAN endpoint 1");
        client.PendingService = new TaskCompletionSource<HttpServiceStatus>();
        var refresh = panel.RefreshForTestingAsync();
        Assert.True(Named<Button>(panel, "Copy loopback endpoint").Enabled);
        Assert.True(lanCopy.Enabled);
        Assert.False(Button(panel, "Refresh").Enabled);
        client.PendingService.SetResult(client.Service);
        PumpUntilCompleted(refresh);
        Assert.Same(lanCopy, Named<Button>(panel, "Copy LAN endpoint 1"));
        Assert.Equal("http://0.0.0.0:9101/mcp", client.Service.Endpoint);
    });

    [Fact]
    public void OldSnapshotsFallbackToLoopbackAndDiscardLanHintsWhenBoundToLoopback() => RunOnSta(() =>
    {
        var serializer = new JavaScriptSerializer();
        var client = new FakeHostControlClient
        {
            Service = serializer.Deserialize<HttpServiceStatus>("{\"BindMode\":\"all\",\"Port\":9101,\"Endpoint\":\"http://0.0.0.0:9101/mcp\"}")
        };
        using var panel = new BridgeControlPanel(client);
        CompleteRefresh(panel);
        Assert.Equal("http://127.0.0.1:9101/mcp", Named<SelectableAddress>(panel, "Loopback endpoint").Text);
        Assert.DoesNotContain(Descendants(panel).OfType<Button>(), button => button.AccessibleName.StartsWith("Copy LAN"));
        client.Service = AllInterfaceStatus();
        CompleteRefresh(panel);
        var roundTrip = serializer.Deserialize<HttpServiceStatus>(serializer.Serialize(client.Service));
        Assert.Equal(client.Service.LanEndpoints, roundTrip.LanEndpoints);
        client.Service.BindMode = HttpBindModes.Loopback;
        CompleteRefresh(panel);
        Assert.Equal(string.Empty, panel.ServiceErrorText);
        Assert.False(Named<LanEndpointList>(panel, "LAN endpoint hints").Visible);
        Assert.DoesNotContain(Descendants(panel).OfType<Button>(), button => button.AccessibleName.StartsWith("Copy LAN"));
    });

    [Fact]
    public void UntrustedLanHintsAreFilteredDeduplicatedAndBounded() => RunOnSta(() =>
    {
        var status = AllInterfaceStatus();
        status.LanEndpoints = new[]
        {
            "http://0.0.0.0:9101/mcp", "http://127.0.0.1:9101/mcp", "http://[::1]:9101/mcp",
            "http://224.0.0.1:9101/mcp", "http://192.168.1.4:9000/mcp", "https://192.168.1.4:9101/mcp",
            "http://secret@192.168.1.4:9101/mcp", "http://192.168.1.4:9101/mcp?token=secret"
        }.Concat(Enumerable.Range(1, 20).SelectMany(index => new[] { $"http://10.0.0.{index}:9101/mcp", $"http://10.0.0.{index}:9101/mcp" })).ToArray();
        using var panel = new BridgeControlPanel(new FakeHostControlClient { Service = status });
        CompleteRefresh(panel);
        var copies = Descendants(panel).OfType<Button>().Where(button => button.AccessibleName.StartsWith("Copy LAN")).ToArray();
        Assert.Equal(8, copies.Length);
        Assert.All(copies, button => Assert.StartsWith("http://10.0.0.", button.AccessibleDescription));
        Assert.Equal(8, copies.Select(button => button.AccessibleDescription).Distinct().Count());
    });

    [Fact]
    public void ControlsHaveExplicitNamesAndLogicalKeyboardOrder() => RunOnSta(() =>
    {
        using var panel = new BridgeControlPanel(new FakeHostControlClient { Service = AllInterfaceStatus() });
        CompleteRefresh(panel);
        var focusable = KeyboardControls(panel).Where(control => control is System.Windows.Forms.Button or CheckBox or SelectableAddress or ComboBox or NumericUpDown or DataGridView or LinkLabel).ToArray();
        Assert.Equal(new[]
        {
            "Enable MCP HTTP service", "Listener bind mode", "Listener port", "Apply listener settings",
            "Refresh service status", "Loopback endpoint", "Copy loopback endpoint", "LAN endpoint 1", "Copy LAN endpoint 1", "LAN endpoint 2", "Copy LAN endpoint 2",
            "Authorized clients", "Authorize HTTP client", "Deauthorize selected HTTP client", "Rotate default CLI token",
            "Active connections", "Disconnect selected HTTP connection",
            "Bridge pipe path", "Copy bridge pipe path", "Daemon pipe path", "Copy daemon pipe path", "Refresh named pipes",
            "MCP startup mode", "MCP authentication mode", "Save MCP settings", "Project link", "Documentation link"
        }, focusable.Select(control => control.AccessibleName));
        Assert.All(Descendants(panel).Where(control => control is Label or GroupBox), control => Assert.False(string.IsNullOrWhiteSpace(control.AccessibleName)));
        Assert.All(focusable.OfType<Button>(), button => Assert.Contains("&",
            button is ClipboardButton copy && copy.Text.Length == 0 ? copy.MnemonicText : button.Text));
        AssertMnemonicTarget(panel, "&Bind", "Listener bind mode");
        AssertMnemonicTarget(panel, "&Port", "Listener port");
        AssertMnemonicTarget(panel, "&Startup", "MCP startup mode");
        AssertMnemonicTarget(panel, "&Authentication", "MCP authentication mode");
        Assert.False(Named<Label>(panel, "Service error").UseMnemonic);
        Assert.False(Named<Label>(panel, "Component versions").UseMnemonic);
    });

    [Theory]
    [InlineData(8.25f)]
    [InlineData(16f)]
    public void LanRowsFitNarrowPanesAtLargeFonts(float fontSize) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, fontSize);
        using var panel = new BridgeControlPanel(new FakeHostControlClient { Service = AllInterfaceStatus() }) { Font = font };
        CompleteRefresh(panel);
        foreach (var width in new[] { 456, 320 })
        {
            panel.Size = new Size(width, 1000);
            panel.PerformLayout();
            Application.DoEvents();
            AssertContentFits(panel);
            Assert.True(panel.Controls[0].Right <= panel.ClientSize.Width);
        }
    });

    [Theory]
    [InlineData(false, 8.25f)]
    [InlineData(false, 16f)]
    [InlineData(true, 8.25f)]
    [InlineData(true, 16f)]
    public void DialogsHaveNamesMnemonicsKeyboardOrderAndWrapping(bool secret, float fontSize) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, fontSize);
        using var form = secret
            ? TextPromptDialog.CreateSecretForm("A&B " + new string('W', 60), "synthetic-test-token")
            : TextPromptDialog.CreatePromptForm("Authorize MCP HTTP client", "Client name (1 to 64 characters)");
        form.Font = font;
        form.CreateControl();
        foreach (var width in new[] { 560, 320, 280 })
        {
            form.ClientSize = new Size(width, form.ClientSize.Height);
            form.PerformLayout();
            Application.DoEvents();
            AssertContentFits(form);
        }
        Assert.All(Descendants(form).Where(control => control is Label or System.Windows.Forms.Button or TextBox), control => Assert.False(string.IsNullOrWhiteSpace(control.AccessibleName)));
        Assert.All(Descendants(form).OfType<Button>(), button => Assert.Contains("&", button.Text));
        var mnemonics = Descendants(form).Where(control => (control is Label label && label.UseMnemonic || control is System.Windows.Forms.Button)
            && control.Text.Contains("&")).Select(control => char.ToUpperInvariant(control.Text[control.Text.IndexOf('&') + 1])).ToArray();
        Assert.Equal(mnemonics.Length, mnemonics.Distinct().Count());
        var focusable = KeyboardControls(form).Where(control => control is TextBox or System.Windows.Forms.Button).ToArray();
        Assert.Equal(secret ? new[] { "Bearer token", "Copy bearer token", "Close token dialog" }
            : new[] { "Client name", "Authorize client", "Cancel authorization" }, focusable.Select(control => control.AccessibleName));
        Assert.NotNull(form.AcceptButton);
        Assert.NotNull(form.CancelButton);
        AssertMnemonicTarget(form, secret ? "&Token" : "&Client name (1 to 64 characters)", secret ? "Bearer token" : "Client name");
        if (secret)
        {
            Assert.True(Named<TextBox>(form, "Bearer token").ReadOnly);
            Assert.True(Named<Button>(form, "Copy bearer token").Enabled);
            Assert.False(Named<Label>(form, "One-time token instructions").UseMnemonic);
        }
    });

    private static T Named<T>(Control parent, string name) where T : Control =>
        Descendants(parent).OfType<T>().Single(control => control.AccessibleName == name);

    private static IEnumerable<Control> KeyboardControls(Control parent) => parent.Controls.Cast<Control>()
        .OrderBy(control => control.TabIndex).SelectMany(control => new[] { control }.Concat(KeyboardControls(control)));

    private static void AssertMnemonicTarget(Control parent, string caption, string target)
    {
        var label = Descendants(parent).OfType<Label>().Single(control => control.Text == caption);
        Assert.True(label.UseMnemonic);
        Assert.Equal(target, label.Parent!.GetNextControl(label, forward: true).AccessibleName);
    }

    private static void AssertContentFits(Control root)
    {
        if (root is BridgeControlPanel) LayoutAllTabs(root);
        // Dialogs remain hidden during these checks, so inspect their layout regardless of Visible.
        foreach (var control in Descendants(root).Where(control => control is Label or System.Windows.Forms.Button or TextBox))
        {
            if (control is Label { Text.Length: 0 }) continue; // Empty message rows intentionally collapse.
            Assert.True(control.Right <= control.Parent!.ClientSize.Width, $"{control.AccessibleName}: {control.Bounds} / {control.Parent.ClientRectangle}");
            Assert.True(control.Bottom <= control.Parent.ClientSize.Height, $"{control.AccessibleName} extends below its container.");
            if (control is Label)
                Assert.True(control.Height >= control.GetPreferredSize(new Size(control.Width, 0)).Height, $"{control.AccessibleName} needs more wrapping height.");
        }
    }

    private static HttpServiceStatus AllInterfaceStatus() => new HttpServiceStatus
    {
        BindMode = HttpBindModes.All,
        BindAddress = "0.0.0.0",
        Port = 9101,
        Endpoint = "http://0.0.0.0:9101/mcp",
        LoopbackEndpoint = "http://127.0.0.1:9101/mcp",
        LanEndpoints = new[] { "http://192.168.1.4:9101/mcp", "http://10.0.0.4:9101/mcp" }
    };
}
