// Measures wrapped content using the actual space inside a WinForms group heading and padding.
using System.Drawing;
using System.Windows.Forms;

namespace FiddlerClassic.Bridge;

internal sealed class ContentGroupBox : GroupBox
{
    private readonly Control _content;

    /// <summary>Owns one width-constrained content control and grows vertically to fit it.</summary>
    /// <param name="title">The accessible group heading.</param>
    /// <param name="content">Content transferred to this group's control collection.</param>
    public ContentGroupBox(string title, Control content)
    {
        _content = content;
        Text = title;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        content.Dock = DockStyle.Top;
        Controls.Add(content);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        // GroupBox's default measurement does not constrain a docked child's wrapping width
        // to DisplayRectangle. Account for the heading and padding before asking the child.
        var inset = new Size(Width - DisplayRectangle.Width, Height - DisplayRectangle.Height);
        var contentWidth = proposedSize.Width > 0 ? Math.Max(1, proposedSize.Width - inset.Width) : 0;
        var preferred = _content.GetPreferredSize(new Size(contentWidth, 0));
        return new Size(preferred.Width + inset.Width, preferred.Height + inset.Height);
    }
}
