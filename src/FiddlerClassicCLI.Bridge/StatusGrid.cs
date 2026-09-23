// Updates read-only operational rows by stable ID with native sorting and buffered painting.
using System.Drawing;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

internal sealed class StatusGrid : DataGridView
{
    /// <summary>Creates a read-only native grid with buffered painting and Windows selection colors.</summary>
    internal StatusGrid()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        ReadOnly = true;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        MultiSelect = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        RowHeadersVisible = false;
        BorderStyle = BorderStyle.FixedSingle;
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        DefaultCellStyle.Padding = new Padding(3, 2, 3, 2);
        UpdateRowHeight();
        ApplyColors();
    }

    /// <summary>Changes only differing cells and membership. Existing unsorted rows keep their order.</summary>
    /// <param name="snapshot">Complete metadata rows with a unique, nonempty string ID in the first cell.</param>
    /// <exception cref="ArgumentException">The snapshot has invalid row widths or duplicate or missing IDs.</exception>
    internal void UpdateRows(IEnumerable<object[]> snapshot)
    {
        var incoming = new Dictionary<string, object[]>(StringComparer.Ordinal);
        foreach (var values in snapshot)
        {
            if (values.Length != Columns.Count || values[0] is not string id || string.IsNullOrEmpty(id)
                || incoming.ContainsKey(id))
                throw new ArgumentException("Status rows require unique IDs and one value per column.", nameof(snapshot));
            incoming.Add(id, values);
        }
        var existing = Rows.Cast<DataGridViewRow>().ToDictionary(row => (string)row.Tag, StringComparer.Ordinal);
        var removed = existing.Where(pair => !incoming.ContainsKey(pair.Key)).Select(pair => pair.Value).ToArray();
        var changed = incoming.Where(pair => !existing.TryGetValue(pair.Key, out var row)
            || pair.Value.Where((value, index) => !Equals(value, row.Cells[index].Value)).Any()).ToArray();
        if (removed.Length == 0 && changed.Length == 0) return;

        var sortIndex = SortedColumn?.Index ?? -1;
        var sort = removed.Length > 0 || changed.Any(pair => !existing.TryGetValue(pair.Key, out var row)
            || sortIndex >= 0 && !Equals(pair.Value[sortIndex], row.Cells[sortIndex].Value));
        SuspendLayout();
        try
        {
            using var view = new GridRefreshState(this, sort);
            foreach (var row in removed) Rows.Remove(row);
            foreach (var pair in changed)
            {
                if (!existing.TryGetValue(pair.Key, out var row))
                {
                    Rows[Rows.Add(pair.Value)].Tag = pair.Key;
                    continue;
                }
                for (var index = 0; index < pair.Value.Length; index++)
                    if (!Equals(row.Cells[index].Value, pair.Value[index]))
                        row.Cells[index].Value = pair.Value[index];
            }
        }
        finally { ResumeLayout(performLayout: true); }
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplyColors();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateRowHeight();
    }

    private void UpdateRowHeight()
    {
        RowTemplate.Height = Font.Height + 8;
        foreach (DataGridViewRow row in Rows) row.Height = RowTemplate.Height;
    }

    private void ApplyColors()
    {
        BackgroundColor = SystemColors.Window;
        GridColor = SystemColors.ControlLight;
        DefaultCellStyle.BackColor = SystemColors.Window;
        DefaultCellStyle.ForeColor = SystemColors.WindowText;
        DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
        DefaultCellStyle.SelectionForeColor = SystemColors.HighlightText;
    }
}
