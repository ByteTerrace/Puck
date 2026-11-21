namespace Puck.State;

/// <summary>Reads the arena's lattice columns for board queries and patterns.</summary>
public static class ArenaBoards {
    private static bool TryBoard(StateArena arena, int rowOrdinal, out ArenaRowLayout layout, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        layout = (((uint)rowOrdinal < (uint)arena.Layout.RowCount) ? arena.Layout[rowOrdinal] : default);
        if (layout is not { Shape: RowShape.Lattice, Topology: not null }) {
            reason = $"row ordinal {rowOrdinal} is not a lattice row the arena stores";
            return false;
        }

        reason = layout.IsStored ? string.Empty : $"row ordinal {rowOrdinal} holds no board the arena can read";
        return layout.IsStored;
    }

    /// <summary>Evaluates a board query. Queries over the whole board use the supplied scratch span.</summary>
    public static bool TryQuery(StateArena arena, int rowOrdinal, BoardQuery query, int source, Span<long> values, out long result, out string reason, int dynamicTarget = 0) {
        ArgumentNullException.ThrowIfNull(argument: query);
        result = 0L;

        if (!TryBoard(arena, rowOrdinal, out var layout, out reason)) {
            return false;
        }
        var topology = layout.Topology!;

        // Anchoring only translates a topology compiled from the same authored row.
        if (!ReferenceEquals(query.Topology.Source, topology.Source)) {
            reason = $"a board query over {query.Topology.CellCount} cells does not address row ordinal {rowOrdinal}, which lays out {topology.CellCount}";
            return false;
        }
        if (query is BoardAttacksQuery attacks) {
            result = BoardQueries.Attacks(attacks, default, layout.Empty, source, arena, rowOrdinal);
            return true;
        }
        if (!arena.TryReadBoard(rowOrdinal, values)) {
            reason = $"a board read over {topology.CellCount} cells needs a span that long";
            return false;
        }

        result = BoardQueries.Evaluate(query, values[..topology.CellCount], layout.Empty, source, dynamicTarget, arena.Scratch);
        return true;
    }

    /// <summary>Reads a board ray directly from the arena. When supplied, <paramref name="cells"/> receives the
    /// matching cell ordinals. Both buffers must hold the longest ray in the direction.</summary>
    public static bool TryReadRay(StateArena arena, int rowOrdinal, int origin, int direction, Span<long> word, out ReadOnlySpan<long> ray, out string reason, scoped Span<int> cells = default) {
        ray = default;

        if (!TryBoard(arena, rowOrdinal, out var layout, out reason)) {
            return false;
        }
        var topology = layout.Topology!;

        if ((uint)direction >= topology.DirectionCount) {
            reason = $"direction ordinal {direction} is not one of the {topology.DirectionCount} the topology steps in";
            return false;
        }
        var capacity = topology.LongestRay(direction: direction);
        if (word.Length < capacity || (!cells.IsEmpty && cells.Length < capacity)) {
            reason = $"a ray of up to {capacity} cells needs buffers that long";
            return false;
        }

        var length = 0;
        foreach (var cell in topology.Ray(origin: origin, direction: direction)) {
            _ = arena.TryReadBoardCell(rowOrdinal, cell, out word[length]);
            if (!cells.IsEmpty) {
                cells[length] = cell;
            }
            length++;
        }
        ray = word[..length];
        return true;
    }
}
