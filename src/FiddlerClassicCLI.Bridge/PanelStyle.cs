// Defines shared native-control spacing and font-based action sizing for the management UI.
using System.Drawing;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal static class PanelStyle
{
    internal static readonly Padding SectionPadding = new Padding(10);
    internal static readonly Padding PagePadding = new Padding(8);
    internal static readonly Padding CaptionMargin = new Padding(3, 4, 12, 4);
    internal static readonly Padding ValueMargin = new Padding(3, 4, 3, 4);
    internal static readonly Padding ActionMargin = new Padding(3);

    internal static int GlyphSize(Font font) => 16 * Math.Max(1, (int)Math.Round(font.Height / 16d));
    internal static int ButtonHeight(Font font) => Math.Max(font.Height, GlyphSize(font)) + 10;

    /// <summary>Creates a wrapping row shared by panel actions and modal dialogs.</summary>
    /// <param name="buttons">Actions in their visible and keyboard navigation order.</param>
    /// <returns>A row that owns the supplied buttons.</returns>
    internal static FlowLayoutPanel CreateActions(params Button[] buttons)
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
        {
            buttons[index].TabIndex = index;
            buttons[index].Margin = ActionMargin;
        }
        actions.Controls.AddRange(buttons);
        return actions;
    }
}
