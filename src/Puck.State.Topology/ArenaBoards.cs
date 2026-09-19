namespace Puck.State;

/// <summary>The board queries a rule asks of a <see cref="StateArena"/>: the arena-facing half of
/// <see cref="BoardQueries"/>, which reads a caller-owned span. Every entry point fills that span from the arena's
/// own columns and hands it to the one kernel, so a query answers the same over an arena as over a document
/// row.</summary>
public static class ArenaBoards {
    private static bool TryBoard(StateArena arena, int rowOrdinal, Span<long> values, out ArenaRowLayout layout, out CompiledTopology topology, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        layout = ((((uint)rowOrdinal) < ((uint)arena.Layout.RowCount))
            ? arena.Layout[rowOrdinal]
            : default
        );

        if (
            (layout.Shape != RowShape.Lattice) ||
            (layout.Topology is null)
        ) {
            reason = $"row ordinal {rowOrdinal} is not a lattice row the arena stores";
            topology = null!;

            return false;
        }

        topology = layout.Topology;

        if (values.Length < topology.CellCount) {
            reason = $"a board read over {topology.CellCount} cells needs a span that long";

            return false;
        }
        if (!arena.TryReadBoard(
            rowOrdinal: rowOrdinal,
            values: values
        )) {
            reason = $"row ordinal {rowOrdinal} holds no board the arena can read";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    /// <summary>Evaluates one board query over an arena row.</summary>
    /// <param name="arena">The arena holding the board.</param>
    /// <param name="rowOrdinal">The board row's catalog ordinal.</param>
    /// <param name="query">The query.</param>
    /// <param name="source">The key cell the query starts at, or <c>-1</c> for none.</param>
    /// <param name="values">Scratch storage of at least the topology's cell count.</param>
    /// <param name="result">The query's answer, on success.</param>
    /// <param name="reason">Why the query was refused, or empty on success.</param>
    /// <param name="dynamicTarget">The live target cell a path-cost query reads, when it declares one.</param>
    /// <returns><see langword="true"/> when the query answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> or <paramref name="query"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryEvaluate(StateArena arena, int rowOrdinal, BoardQuery query, int source, Span<long> values, out long result, out string reason, int dynamicTarget = 0) {
        ArgumentNullException.ThrowIfNull(argument: query);

        result = 0L;

        if (!TryBoard(
            arena: arena,
            layout: out var layout,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            topology: out var topology,
            values: values
        )) {
            return false;
        }
        // The query's own compile may be anchored where the row's layout compiles unanchored; the two address one
        // board when they compiled from one authored topology, which anchoring only translates.
        if (!ReferenceEquals(
            objA: query.Topology.Source,
            objB: topology.Source
        )) {
            reason = $"a board query over {query.Topology.CellCount} cells does not address row ordinal {rowOrdinal}, which lays out {topology.CellCount}";

            return false;
        }

        result = BoardQueries.Evaluate(
            dynamicTarget: dynamicTarget,
            empty: layout.Empty,
            query: query,
            source: source,
            values: values[..topology.CellCount]
        );

        return true;
    }
    /// <summary>Reads the word a pattern walks along one ray of an arena board: the cells from the origin
    /// (exclusive) in one direction, stopping at the edge or on return to the origin.</summary>
    /// <param name="arena">The arena holding the board.</param>
    /// <param name="rowOrdinal">The board row's catalog ordinal.</param>
    /// <param name="origin">The origin cell, or <c>-1</c> for none.</param>
    /// <param name="direction">The direction ordinal; one the topology steps in, or the ray is refused.</param>
    /// <param name="values">Scratch storage of at least the topology's cell count.</param>
    /// <param name="word">The word buffer, at least the topology's cell count long.</param>
    /// <param name="ray">The word and the read that produced it, on success.</param>
    /// <param name="reason">Why the ray was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the ray was read.</returns>
    /// <remarks>Each origin and direction reads its own word off the one board, and the word carries which of them
    /// it came from.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static bool TryReadRay(StateArena arena, int rowOrdinal, int origin, int direction, Span<long> values, Span<long> word, out ArenaWord ray, out string reason) {
        ray = default;

        if (!TryBoard(
            arena: arena,
            layout: out _,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            topology: out var topology,
            values: values
        )) {
            return false;
        }
        if (word.Length < topology.CellCount) {
            reason = $"a ray over {topology.CellCount} cells needs a word buffer that long";

            return false;
        }
        if (((uint)direction) >= topology.DirectionCount) {
            reason = $"direction ordinal {direction} is not one of the {topology.DirectionCount} the topology steps in";

            return false;
        }

        var length = BoardQueries.ReadRay(
            direction: direction,
            origin: origin,
            topology: topology,
            values: values[..topology.CellCount],
            word: word
        );

        ray = new ArenaWord(
            arena: arena,
            letters: word[..length],
            source: new WordSource(
                AttributeOrdinal: -1,
                Direction: direction,
                RowOrdinal: rowOrdinal,
                Start: origin
            )
        );

        return true;
    }
}
