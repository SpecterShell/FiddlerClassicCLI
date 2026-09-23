// Checks inline URL actions, accessible listener state, and native combo text bounds at enlarged font scales.
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Fact]
    public void CompactCopyRetainsItsKeyboardShortcutAndExpandsAgain() => RunOnSta(() =>
    {
        var label = new SelectableAddress { Text = "http://127.0.0.1:8877/mcp" };
        var copy = new ClipboardButton { AccessibleName = "Copy test URL" };
        using var row = new EndpointRow(label, copy) { Size = new Size(100, 150) };
        row.CreateControl();
        row.PerformLayout();
        Assert.Empty(copy.Text);
        Assert.True(copy.TabStop);
        var clicked = false;
        copy.Click += (_, _) => clicked = true;
        var mnemonic = typeof(ClipboardButton).GetMethod("ProcessMnemonic",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.True((bool)mnemonic.Invoke(copy, new object[] { 'c' })!);
        Assert.True(clicked);
        row.Width = 600;
        row.PerformLayout();
        Assert.Equal("&Copy", copy.Text);
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void EndpointCopiesStayBesideTheirUrlsWithScaledClipboardImages(float scale) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * scale);
        var client = new FakeHostControlClient { Service = AllInterfaceStatus() };
        client.Service.Enabled = true;
        client.Service.Running = true;
        using var panel = new BridgeControlPanel(client) { Font = font };
        panel.CreateControl();
        PumpUntilIdle(panel);
        CompleteRefresh(panel);
        foreach (var width in new[] { 1200, 640, 456, 320, 900 })
        {
            panel.Size = new Size(width, 1300);
            panel.CreateControl();
            panel.PerformLayout();
            Application.DoEvents();
            AssertContentFits(panel);
            var rows = Descendants(Named<GroupBox>(panel, "MCP addresses")).OfType<EndpointRow>().ToArray();
            Assert.Equal(3, rows.Length);
            foreach (var row in rows)
            {
                var label = Assert.Single(row.Controls.OfType<SelectableAddress>());
                var copy = Assert.Single(row.Controls.OfType<ClipboardButton>());
                Assert.True(copy.Left >= label.Right, "Each copy button must be to the right of its URL.");
                Assert.True(label.Width <= label.GetPreferredSize(Size.Empty).Width,
                    "Spare pane width must remain after the copy button, not between it and the URL.");
                Assert.InRange(copy.Left - label.Right, 0, 12);
                Assert.True(copy.Top < label.Bottom && label.Top < copy.Bottom, "A copy action must share its URL's row.");
                Assert.True(row.ClientRectangle.Contains(copy.Bounds));
                Assert.Equal(label.Text, copy.AccessibleDescription);
                if (width >= 640) Assert.NotEmpty(copy.Text);
                Assert.NotNull(copy.Image);
                Assert.Equal(0, copy.Image.Width % 16);
                Assert.True(copy.Image.Width >= font.Height / 2, "The clipboard glyph must remain legible at large fonts.");
                var textWidth = TextRenderer.MeasureText(copy.Text, copy.Font, Size.Empty, TextFormatFlags.SingleLine).Width;
                Assert.True(copy.ClientSize.Width >= textWidth + copy.Image.Width + copy.Padding.Horizontal + 8,
                    "The clipboard image must leave enough space for a single line of button text.");
            }
            SaveServiceSnapshot(panel, $"listening-{scale}-{width}");
        }
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void BindChoicesFitTheNativeComboTextRectangle(float scale) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * scale);
        using var panel = new BridgeControlPanel(new FakeHostControlClient()) { Size = new Size(640, 1300) };
        panel.CreateControl();
        PumpUntilIdle(panel);
        CompleteRefresh(panel);
        // Change the inherited font after handle creation, as an existing host can do during scaling.
        var bind = Named<ComboBox>(panel, "Listener bind mode");
        _ = bind.Handle;
        panel.Font = font;
        panel.PerformLayout();
        Application.DoEvents();
        foreach (var item in bind.Items.Cast<string>())
        {
            bind.SelectedItem = item;
            AssertNativeChoicesFit(bind);
        }
        Assert.True(bind.DropDownWidth >= bind.Width);
        bind.SelectedItem = HttpBindModes.Loopback;
        var fittedWidth = bind.Width;
        ((ContentSizedComboBox)bind).FitChoices();
        Assert.Equal(fittedWidth, bind.Width);
        SaveServiceSnapshot(panel, $"disabled-{scale}");
    });

    [Fact]
    public void CheckboxRetainsAccessibleListeningStoppedAndUnknownDetailsWithoutAStatusRow() => RunOnSta(() =>
    {
        var client = new FakeHostControlClient();
        using var panel = new BridgeControlPanel(client) { Size = new Size(640, 1000) };
        var checkbox = Named<CheckBox>(panel, "Enable MCP HTTP service");
        CompleteRefresh(panel);
        Assert.False(checkbox.Checked);
        Assert.Equal("Disabled", panel.ServiceStateText);
        client.Service.Enabled = true;
        CompleteRefresh(panel);
        Assert.True(checkbox.Checked);
        Assert.Equal("Enabled, stopped", panel.ServiceStateText);
        client.Service.Running = true;
        CompleteRefresh(panel);
        Assert.True(checkbox.Checked);
        Assert.StartsWith("Listening on", panel.ServiceStateText);
        Assert.Equal(panel.ServiceStateText, checkbox.AccessibleDescription);
        client.ServiceFailure = new TimeoutException("test status timeout");
        CompleteRefresh(panel);
        Assert.Equal("Status unavailable", panel.ServiceStateText);
        client.ServiceFailure = null;
        CompleteRefresh(panel);
        Assert.StartsWith("Listening on", checkbox.AccessibleDescription);
    });

    private static void SaveServiceSnapshot(Control panel, string suffix)
        => SaveLayoutSnapshot(Named<TabPage>(panel, "MCP"), $"service-{suffix}");

    private static void SaveLayoutSnapshot(Control control, string name)
    {
        var directory = Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_LAYOUT_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        bitmap.Save(Path.Combine(directory, $"{name}.png"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public NativeRectangle Item, Button;
        public int ButtonState;
        public IntPtr ComboHandle, ItemHandle, ListHandle;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr combo, ref ComboBoxInfo info);
}
