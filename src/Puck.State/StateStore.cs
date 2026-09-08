namespace Puck.State;

/// <summary>Where a rule read finds a cell's stored value. The rows carry the structure every read resolves through —
/// keys, domains, value-over-time traits — and the store answers the stored raw beneath a key: the row's own cell
/// list for the installed section, or a value frame laid over the same rows for a hypothetical evaluation. Every
/// member is read on the tick path and allocates nothing.</summary>
public abstract class StateStore {
    /// <summary>Gets the rows whose structure every read resolves through.</summary>
    public abstract IReadOnlyList<StateRow> Rows { get; }

    /// <summary>Reads the stored raw value and text beneath a key.</summary>
    /// <param name="row">The row, one of <see cref="Rows"/>.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The stored raw value, in the row's encoding.</param>
    /// <param name="text">The stored text of a text row's cell, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row holds a cell under that key.</returns>
    public abstract bool TryStored(StateRow row, CellName key, out long value, out string? text);

    /// <summary>Reads a stored value together with its authored cell metadata. Dense cells created only in a frame
    /// may have a value without authored metadata. Stores can override this to resolve the key once.</summary>
    /// <param name="row">The row.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The stored raw value.</param>
    /// <param name="text">The stored text, or null.</param>
    /// <param name="cell">The authored cell carrying behavior traits, or null.</param>
    /// <returns>Whether the store holds the cell.</returns>
    public virtual bool TryStored(StateRow row, CellName key, out long value, out string? text, out StateCell? cell) {
        var found = TryStored(row, key, out value, out text);
        cell = found ? StateRows.FindCell(row.Cells, key) : null;
        return found;
    }

    /// <summary>Reads the stored raw value of the cell at a position of the row's cell order.</summary>
    /// <param name="row">The row.</param>
    /// <param name="index">The position in <see cref="StateRow.Cells"/> order — for a ring, the slot.</param>
    /// <param name="value">The stored raw value.</param>
    /// <returns><see langword="true"/> when the store holds a cell at that position.</returns>
    public abstract bool TryStoredAt(StateRow row, int index, out long value);

    /// <summary>Returns how many cells a row holds through this store: an ordered zone's live membership inside a
    /// frame, else the row's own cell count.</summary>
    /// <param name="row">The row.</param>
    public virtual int CellCount(StateRow row) => (row?.Cells?.Count ?? 0);

    /// <summary>Reads the key of a row's cell by position — the ordinal that <see cref="TryStoredAt"/> reads the
    /// value of, so a zone's members enumerate in pile order through a frame that has moved them.</summary>
    /// <param name="row">The row.</param>
    /// <param name="index">The cell's position.</param>
    /// <param name="key">The cell's key.</param>
    public virtual bool TryKeyAt(StateRow row, int index, out CellName key) {
        var cells = row?.Cells;

        if ((cells is null) || (((uint)index) >= ((uint)cells.Count))) {
            key = default;

            return false;
        }

        key = cells[index].Key;

        return true;
    }

    /// <summary>Returns how many values a ring row has ever been pushed.</summary>
    /// <param name="row">The ring row.</param>
    public abstract long HistoryCursor(StateRow row);

    /// <summary>Reads a board row as one value per topology cell; a cell the row does not hold reads the board's empty value.</summary>
    /// <param name="row">The board row.</param>
    /// <param name="topology">The row's topology.</param>
    /// <param name="values">Scratch of at least the topology's cell count.</param>
    public abstract void ReadBoard(StateRow row, CompiledTopology topology, Span<long> values);

    /// <summary>Finds a row by name.</summary>
    /// <param name="name">The row name.</param>
    public StateRow? Find(string name) => StateRows.FindStateRow(rows: Rows, name: name);
}

/// <summary>The store over a section's own rows: a cell's stored value is its <see cref="StateCell.Value"/>. The rows
/// are read through a delegate so a host that replaces its section on every install keeps one store.</summary>
public sealed class RowStore : StateStore {
    private readonly Func<IReadOnlyList<StateRow>> m_rows;

    /// <summary>Initializes a store over rows read fresh on every access.</summary>
    /// <param name="rows">Returns the current rows.</param>
    public RowStore(Func<IReadOnlyList<StateRow>> rows) {
        ArgumentNullException.ThrowIfNull(argument: rows);
        m_rows = rows;
    }
    /// <summary>Initializes a store over one fixed row list.</summary>
    /// <param name="rows">The rows.</param>
    public RowStore(IReadOnlyList<StateRow> rows) : this(rows: () => rows) {
        ArgumentNullException.ThrowIfNull(argument: rows);
    }

    /// <summary>Gets the store over no rows.</summary>
    public static RowStore Empty { get; } = new(rows: []);

    /// <inheritdoc/>
    public override IReadOnlyList<StateRow> Rows => m_rows();

    /// <inheritdoc/>
    public override bool TryStored(StateRow row, CellName key, out long value, out string? text) => Stored(row: row, key: key, value: out value, text: out text);
    /// <inheritdoc/>
    public override bool TryStored(StateRow row, CellName key, out long value, out string? text, out StateCell? cell) {
        cell = StateRows.FindCell(row.Cells, key);
        value = cell?.Value ?? 0L;
        text = cell?.Text;
        return cell is not null;
    }
    /// <inheritdoc/>
    public override bool TryStoredAt(StateRow row, int index, out long value) {
        var cells = row.Cells;

        if ((cells is null) || (((uint)index) >= ((uint)cells.Count))) {
            value = 0L;

            return false;
        }

        value = cells[index].Value;

        return true;
    }
    /// <inheritdoc/>
    public override long HistoryCursor(StateRow row) => row.HistoryCursor;
    /// <inheritdoc/>
    public override void ReadBoard(StateRow row, CompiledTopology topology, Span<long> values) => BoardQueries.Read(row: row, topology: topology, values: values);

    /// <summary>Reads the stored value beneath a key of a row's own cell list.</summary>
    /// <param name="row">The row.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The stored raw value.</param>
    /// <param name="text">The stored text, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row holds the cell.</returns>
    public static bool Stored(StateRow row, CellName key, out long value, out string? text) {
        if (StateRows.FindCell(cells: row.Cells, key: key) is { } cell) {
            value = cell.Value;
            text = cell.Text;

            return true;
        }

        value = 0L;
        text = null;

        return false;
    }
}
