// Preserves selection, sorting, and viewport across an incremental status-grid update.
using System.ComponentModel;
using System.Windows.Forms;

namespace FiddlerClassicCLI.Bridge;

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
    private readonly bool _sortRows;

    /// <summary>Snapshots the view before changing rows, whose tags must be stable IDs.</summary>
    /// <param name="grid">The UI-thread-owned status grid.</param>
    /// <param name="sortRows">Whether membership or values in the sorted column changed.</param>
    public GridRefreshState(DataGridView grid, bool sortRows)
    {
        _grid = grid;
        _sortRows = sortRows;
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
        if (_sortRows && _sortedColumn is not null && _sortOrder != SortOrder.None)
        {
            _grid.Sort(_sortedColumn, _sortOrder == SortOrder.Ascending
                ? ListSortDirection.Ascending : ListSortDirection.Descending);
        }

        var selected = FindRow(_selectedId);
        var cell = selected?.Cells[_columnIndex];
        if (_grid.CurrentCell != cell) _grid.CurrentCell = cell;
        if (selected is null)
        {
            if (_grid.SelectedRows.Count > 0) _grid.ClearSelection();
        }
        else if (!selected.Selected)
        {
            _grid.ClearSelection();
            selected.Selected = true;
        }

        // A vanished selection stays empty. It must never move to another destructive-action target.
        if (_firstRowIndex >= 0 && _grid.RowCount > 0)
        {
            var first = FindRow(_firstRowId);
            var index = first?.Index ?? Math.Min(_firstRowIndex, _grid.RowCount - 1);
            if (_grid.FirstDisplayedScrollingRowIndex != index) _grid.FirstDisplayedScrollingRowIndex = index;
        }

        if (_grid.HorizontalScrollingOffset != _horizontalOffset) _grid.HorizontalScrollingOffset = _horizontalOffset;
    }

    private DataGridViewRow? FindRow(string? id) => id is null ? null
        : _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => string.Equals(row.Tag as string, id, StringComparison.Ordinal));
}
