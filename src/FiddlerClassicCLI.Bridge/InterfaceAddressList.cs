// Lists host-reported IPv4 interfaces while retaining unsaved checks across status refreshes.
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed class InterfaceAddressList : CheckedListBox
{
    private bool _updating;
    internal event EventHandler? SelectionEdited;

    internal InterfaceAddressList()
    {
        AccessibleName = "Selected IPv4 interfaces";
        // Match surrounding native controls even if the host enables legacy GDI+ text globally.
        UseCompatibleTextRendering = false;
        CheckOnClick = true;
        HorizontalScrollbar = true;
        Height = 110;
        Visible = false;
    }

    internal string[] SelectedAddresses => CheckedItems.Cast<Choice>().Select(item => item.Address).ToArray();

    /// <summary>Updates available adapters without dropping a checked address that has gone offline.</summary>
    /// <param name="available">Host-provided active interface metadata, without network probing.</param>
    /// <param name="selected">Saved addresses, or the current unsaved selection.</param>
    internal void UpdateAddresses(HttpInterfaceAddressDto[] available, string[] selected)
    {
        var choices = available.Take(HttpListenerLimits.MaximumAvailableInterfaces).Where(item => ValidAddress(item.Address))
            .GroupBy(item => item.Address, StringComparer.Ordinal).Select(group => group.First())
            .Select(item => new Choice(item.Address, item.AdapterName)).ToList();
        foreach (var address in selected.Where(ValidAddress))
            if (choices.All(item => item.Address != address)) choices.Add(new Choice(address, "unavailable"));
        var old = Items.Cast<Choice>().ToArray();
        if (old.Select(item => item.ToString()).SequenceEqual(choices.Select(item => item.ToString()))
            && new HashSet<string>(SelectedAddresses, StringComparer.Ordinal).SetEquals(selected)) return;
        var focused = (SelectedItem as Choice)?.Address;
        var top = TopIndex;
        _updating = true;
        BeginUpdate();
        try
        {
            Items.Clear();
            foreach (var choice in choices) Items.Add(choice, selected.Contains(choice.Address));
            SelectedIndex = choices.FindIndex(item => item.Address == focused);
            if (Items.Count > 0) TopIndex = Math.Min(top, Items.Count - 1);
        }
        finally { EndUpdate(); _updating = false; }
    }

    protected override void OnItemCheck(ItemCheckEventArgs e)
    {
        if (!_updating && e.NewValue == CheckState.Checked && e.CurrentValue != CheckState.Checked
            && CheckedItems.Count >= HttpListenerLimits.MaximumSelectedAddresses)
        {
            e.NewValue = e.CurrentValue;
            return;
        }
        base.OnItemCheck(e);
        if (!_updating) SelectionEdited?.Invoke(this, EventArgs.Empty);
    }

    private static bool ValidAddress(string value) => IPAddress.TryParse(value, out var address)
        && address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes()[0] > 0 && address.GetAddressBytes()[0] < 224;

    private sealed class Choice
    {
        internal Choice(string address, string adapter) { Address = address; Adapter = adapter; }
        internal string Address { get; }
        private string Adapter { get; }
        public override string ToString() => string.IsNullOrWhiteSpace(Adapter) ? Address : $"{Address} ({Adapter})";
    }
}
