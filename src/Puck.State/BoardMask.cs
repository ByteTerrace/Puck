using Puck.Maths;

namespace Puck.State;

/// <summary>The most cells a board may hold for its occupancy to read as one 64-bit mask, and the value planes the
/// arena keeps for such a board.</summary>
public static class BoardMask {
    /// <summary>Cell ordinals 0..63 map to bits 0..63.</summary>
    public const int MaxCells = 64;

    /// <summary>Returns the window of values a board row's planes span, read from its declarations alone: every value
    /// a write to the row can store, and the empty value an absent cell reads as. The arena keeps a
    /// <see cref="BitmapIndex64"/> over exactly this window for every stored board of at most <see cref="MaxCells"/>
    /// cells that has one, and the work sheet prices a mask read over the same window, so the two cannot
    /// disagree.</summary>
    /// <param name="catalog">The catalog the row is declared in.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="row">The row's declaration.</param>
    /// <param name="lower">The window's least value, or zero when there is none.</param>
    /// <param name="width">How many values the window spans, or zero when there is none.</param>
    /// <returns><see langword="true"/> when the row is an integer or boolean board in the document lane whose values
    /// and empty value fit in <see cref="BitmapIndex64.MaxWidth"/> consecutive values.</returns>
    public static bool TryPlaneWindow(StateCatalog catalog, int rowOrdinal, StateRowShape row, out long lower, out int width) {
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: row);

        lower = 0L;
        width = 0;
        if (
            (((uint)rowOrdinal) >= ((uint)catalog.Count)) ||
            (catalog.Descriptors[rowOrdinal] is not { Lane: StateLane.Document, HostOwned: false } descriptor) ||
            (row.Kind is not (CellKind.Int or CellKind.Bool)) ||
            (row.EffectiveDomain is not StateDomain.CellsOf board)
        ) {
            return false;
        }

        _ = catalog.TryGetEnum(handle: descriptor.Handle, symbols: out var symbols);
        var (admittedLower, admittedUpper) = row.AdmittedRange(symbols: symbols);
        var from = Math.Min(val1: admittedLower, val2: board.Empty);
        var to = Math.Max(val1: admittedUpper, val2: board.Empty);

        // The span is compared unsigned, so an unbounded row's whole-long span reads as too wide rather than wrapping.
        if (unchecked((ulong)to - (ulong)from) >= BitmapIndex64.MaxWidth) {
            return false;
        }

        lower = from;
        width = ((int)(to - from) + 1);
        return true;
    }
}
