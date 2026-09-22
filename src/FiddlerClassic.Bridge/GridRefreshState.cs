// Preserves selection, sorting, and viewport when a status grid replaces its snapshot.
using System.ComponentModel;
using System.Windows.Forms;

namespace FiddlerClassic.Bridge;

internal sealed class GridRefreshState : IDisposable
{
    private readonly DataGridView _grid;
    private readonly string? _selectedId;
    private readonly string? _firstRowId;
    private readonly int _firstRowIndex;
    private readonly int _columnIndex;
    private readonly int _horizontalOffset;
    private readonly DataGridViewColumn? _sortedColumn;
    private readonly SortOrder _sortOrder;

    /// <summary>Snapshots the view before rebuilding rows, whose tags must be stable IDs.</summary>
    /// <param name="grid">The UI-thread-owned status grid.</param>
    public GridRefreshState(DataGridView grid)
    {
        _grid = grid;
        _selectedId = grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as string;
        _firstRowIndex = grid.FirstDisplayedScrollingRowIndex;
        _firstRowId = _firstRowIndex < 0 ? null : grid.Rows[_firstRowIndex].Tag as string;
        _columnIndex = grid.CurrentCell?.ColumnIndex ?? 0;
        _horizontalOffset = grid.HorizontalScrollingOffset;
        _sortedColumn = grid.SortedColumn;
        _sortOrder = grid.SortOrder;
    }

    public void Dispose()
    {
        if (_sortedColumn is not null && _sortOrder != SortOrder.None)
        {
            _grid.Sort(_sortedColumn, _sortOrder == SortOrder.Ascending
                ? ListSortDirection.Ascending : ListSortDirection.Descending);
        }

        _grid.CurrentCell = null;
        _grid.ClearSelection();
        var selected = FindRow(_selectedId);
        if (selected is not null)
        {
            _grid.CurrentCell = selected.Cells[_columnIndex];
            selected.Selected = true;
        }

        // A vanished selection stays empty; it must never move to another destructive-action target.
        if (_firstRowIndex >= 0 && _grid.RowCount > 0)
        {
            var first = FindRow(_firstRowId);
            _grid.FirstDisplayedScrollingRowIndex = first?.Index ?? Math.Min(_firstRowIndex, _grid.RowCount - 1);
        }

        _grid.HorizontalScrollingOffset = _horizontalOffset;
    }

    private DataGridViewRow? FindRow(string? id) => id is null ? null
        : _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => string.Equals(row.Tag as string, id, StringComparison.Ordinal));
}
