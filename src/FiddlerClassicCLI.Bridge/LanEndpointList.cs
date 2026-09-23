// Displays bounded host-provided LAN address hints with an independent, read-only copy action per URL.
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed class LanEndpointList : TableLayoutPanel
{
    private string[] _endpoints = Array.Empty<string>();
    private bool _showLan;

    internal LanEndpointList()
    {
        AutoSize = true;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Margin = Padding.Empty;
        AccessibleName = "LAN endpoint hints";
        TabStop = false;
        Visible = false;
    }

    /// <summary>Rebuilds only when hints change, preserving copy-button focus across periodic refreshes.</summary>
    /// <param name="status">A host snapshot. This control never enumerates adapters or probes the network.</param>
    internal void UpdateEndpoints(HttpServiceStatus status)
    {
        var showLan = status.BindMode == HttpBindModes.All;
        var endpoints = showLan
            ? (status.LanEndpoints ?? Array.Empty<string>()).Take(128)
                .Where(endpoint => IsLanEndpoint(endpoint, status.Port)).Distinct(StringComparer.Ordinal).Take(8).ToArray()
            : Array.Empty<string>();
        if (_showLan == showLan && _endpoints.SequenceEqual(endpoints, StringComparer.Ordinal))
            return;

        _showLan = showLan;
        _endpoints = endpoints;
        SuspendLayout();
        try
        {
            while (Controls.Count > 0)
                Controls[0].Dispose();
            RowStyles.Clear();
            RowCount = 0;
            AddRow(new Label
            {
                AutoSize = true,
                UseMnemonic = false,
                AccessibleName = "LAN address availability",
                Text = endpoints.Length == 0
                    ? "No LAN IPv4 URLs were reported. Remote reachability has not been tested."
                    : "Active adapter IPv4 URLs (up to 8). Remote reachability depends on routing and firewall settings. It has not been tested."
            });
            for (var index = 0; index < endpoints.Length; index++)
            {
                var endpoint = endpoints[index];
                var label = new Label { AutoSize = true, Text = endpoint, UseMnemonic = false, AccessibleName = $"LAN endpoint {index + 1}" };
                var copy = new ClipboardButton
                {
                    Text = $"Copy &{index + 1}",
                    AccessibleName = $"Copy LAN endpoint {index + 1}",
                    AccessibleDescription = endpoint,
                    AutoSize = true
                };
                copy.Click += (_, _) => Clipboard.SetText(endpoint);
                AddRow(new EndpointRow(label, copy));
            }
            Visible = showLan;
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    private void AddRow(Control control)
    {
        var row = RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.TabIndex = row;
        if (control is Label)
            control.Dock = DockStyle.Fill;
        Controls.Add(control, 0, row);
    }

    private static bool IsLanEndpoint(string endpoint, int port)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || uri.Port != port || uri.AbsolutePath != "/mcp"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !IPAddress.TryParse(uri.Host, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] > 0 && bytes[0] < 224;
    }
}
