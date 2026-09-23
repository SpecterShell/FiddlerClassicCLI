// Provides small modal dialogs for client names and one-time bearer token display.
using System.Drawing;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal static class TextPromptDialog
{
    public static string? Ask(IWin32Window owner, string title, string prompt)
    {
        using (var form = CreatePromptForm(title, prompt))
        {
            var input = (TextBox)form.Controls.Find("promptInput", searchAllChildren: true)[0];
            return form.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
        }
    }

    public static void ShowSecret(IWin32Window owner, string clientName, string token)
    {
        using (var form = CreateSecretForm(clientName, token))
        {
            form.ShowDialog(owner);
        }
    }

    /// <summary>Builds the name prompt without showing a modal window, allowing offline layout checks.</summary>
    /// <param name="title">The dialog title and accessible name.</param>
    /// <param name="prompt">The name-field caption, with an optional mnemonic.</param>
    /// <returns>A form owned by the caller.</returns>
    internal static Form CreatePromptForm(string title, string prompt)
    {
        var input = new TextBox { Name = "promptInput", AccessibleName = "Client name", MaxLength = 64 };
        var ok = new PanelButton { Text = "&OK", AccessibleName = "Authorize client", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new PanelButton { Text = "Ca&ncel", AccessibleName = "Cancel authorization", DialogResult = DialogResult.Cancel, AutoSize = true };
        var form = CreateForm(title, 420,
            new Label { Text = prompt.Contains("&") ? prompt : "&" + prompt, AccessibleName = "Client name prompt", AutoSize = true },
            input, PanelStyle.CreateActions(ok, cancel));
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => input.Focus();
        return form;
    }

    /// <summary>Builds the one-time token view with wrapping instructions and keyboard-copyable text.</summary>
    /// <param name="clientName">Display text, never interpreted as a mnemonic.</param>
    /// <param name="token">The one-time token, retained only for the lifetime of this caller-owned form.</param>
    /// <returns>A form owned by the caller.</returns>
    internal static Form CreateSecretForm(string clientName, string token)
    {
        var value = new TextBox { Text = token, ReadOnly = true, AccessibleName = "Bearer token" };
        var copy = new ClipboardButton
        {
            AccessibleName = "Copy bearer token", Enabled = !string.IsNullOrEmpty(token),
            ToolTipText = "Copy this one-time bearer token to the clipboard.", GetCopyText = () => token
        };
        var close = new PanelButton { Text = "C&lose", AccessibleName = "Close token dialog", DialogResult = DialogResult.OK, AutoSize = true };
        var form = CreateForm("Authorized MCP HTTP client", 560,
            new Label
            {
                Text = $"{clientName} is authorized. Copy this token now. It cannot be shown again.",
                AccessibleName = "One-time token instructions",
                UseMnemonic = false,
                AutoSize = true
            },
            new Label { Text = "&Token", AccessibleName = "Token label", AutoSize = true },
            value, PanelStyle.CreateActions(copy, close));
        form.AcceptButton = close;
        form.CancelButton = close;
        form.Shown += (_, _) => { value.Focus(); value.SelectAll(); };
        return form;
    }

    /// <summary>Constrains content to the dialog width and grows vertically for wrapped text and actions.</summary>
    /// <param name="title">The visible and accessible window title.</param>
    /// <param name="width">The initial client width at the default font size.</param>
    /// <param name="rows">Controls transferred to the form in label and keyboard navigation order.</param>
    /// <returns>A form whose content scrolls if it exceeds the available screen height.</returns>
    private static Form CreateForm(string title, int width, params Control[] rows)
    {
        var form = new Form
        {
            Text = title,
            AccessibleName = title,
            AutoScaleMode = AutoScaleMode.Font,
            AutoScroll = true,
            ClientSize = new Size(width, 160),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = rows.Length,
            Padding = new Padding(12),
            TabStop = false,
            TabIndex = 0
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < rows.Length; index++)
        {
            var control = rows[index];
            control.Dock = DockStyle.Fill;
            control.TabIndex = index;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, index);
        }
        layout.Layout += (_, _) =>
        {
            var height = Math.Min(layout.Height, Screen.FromControl(form).WorkingArea.Height * 3 / 4);
            if (height > 0 && height != form.ClientSize.Height)
                form.ClientSize = new Size(form.ClientSize.Width, height);
        };
        form.Controls.Add(layout);
        return form;
    }
}
