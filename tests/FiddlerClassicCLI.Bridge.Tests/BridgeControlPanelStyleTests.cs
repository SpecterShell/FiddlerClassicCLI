// Checks shared native styling, font-scaled heights, and clipboard alignment without changing Windows settings.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Bridge;

namespace FiddlerClassicCLI.Bridge.Tests;

public sealed partial class BridgeControlPanelTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void PanelAndDialogsShareActionHeightsAndReadableGridRows(float scale) => RunOnSta(() =>
    {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * scale);
        using var panel = new BridgeControlPanel(new FakeHostControlClient { Service = AllInterfaceStatus() })
        { Font = font, Size = new Size(640, 1000) };
        CompleteRefresh(panel);
        panel.PerformLayout();
        using var dialog = TextPromptDialog.CreateSecretForm("Test client", "synthetic-token");
        dialog.Font = font;
        dialog.PerformLayout();
        var buttons = Descendants(panel).Concat(Descendants(dialog)).OfType<Button>().ToArray();
        Assert.All(buttons, button =>
        {
            Assert.Equal(PanelStyle.ButtonHeight(font), button.Height);
            Assert.True(button.UseVisualStyleBackColor);
        });
        Assert.All(Descendants(panel).OfType<Button>(), button => Assert.Equal(PanelStyle.ActionMargin, button.Margin));
        // Top-level forms apply Windows font autoscaling to their shared logical spacing.
        var dialogButtons = Descendants(dialog).OfType<Button>().ToArray();
        Assert.All(dialogButtons, button => Assert.Equal(dialogButtons[0].Margin, button.Margin));
        Assert.True(dialogButtons[0].Margin.Left >= PanelStyle.ActionMargin.Left);
        Assert.All(Descendants(panel).OfType<ContentGroupBox>(), group => Assert.Equal(PanelStyle.SectionPadding, group.Padding));
        foreach (var grid in Descendants(panel).OfType<StatusGrid>())
        {
            grid.Rows.Add("synthetic-id");
            Assert.True(grid.Rows[0].Height >= font.Height + grid.DefaultCellStyle.Padding.Vertical + 2);
            Assert.Equal(SystemColors.Window, grid.BackgroundColor);
            Assert.Equal(SystemColors.WindowText, grid.DefaultCellStyle.ForeColor);
            Assert.Equal(SystemColors.HighlightText, grid.DefaultCellStyle.SelectionForeColor);
            Assert.Equal(DataGridViewCellBorderStyle.SingleHorizontal, grid.CellBorderStyle);
        }
        using var copy = new ClipboardButton { Font = font };
        copy.Compact = true;
        Assert.Equal(ContentAlignment.MiddleCenter, copy.ImageAlign);
        copy.Compact = false;
        Assert.Equal(ContentAlignment.MiddleLeft, copy.ImageAlign);
    });
}
