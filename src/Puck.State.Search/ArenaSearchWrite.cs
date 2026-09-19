namespace Puck.State;

/// <summary>One state write a finished arena search job wants applied, addressed the way a compiled read or write
/// addresses a cell: a catalog row ordinal and an interned <see cref="CellKey"/>, never a pair of strings. A caller
/// installs the writes through whatever mutation door it owns; the search knows only that a job wants them made.</summary>
[Union]
public abstract record ArenaSearchWrite {
    private ArenaSearchWrite() { }

    /// <summary>Sets one cell of a row to a numeric value.</summary>
    /// <param name="RowOrdinal">The row's catalog ordinal.</param>
    /// <param name="Key">The cell key, interned by the arena's catalog.</param>
    /// <param name="Value">The value to store.</param>
    public sealed record Cell(int RowOrdinal, CellKey Key, long Value) : ArenaSearchWrite;
    /// <summary>Clears every cell of a board row, issued before a job repaints it sparsely.</summary>
    /// <param name="RowOrdinal">The row's catalog ordinal.</param>
    public sealed record ClearBoard(int RowOrdinal) : ArenaSearchWrite;
}
