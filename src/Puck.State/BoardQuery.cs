namespace Puck.State;

/// <summary>The finite discrete-board query vocabulary this library answers. A ray's first-blocker cell, its
/// distance, and a run-length existence check are <c>$match:</c> facets over a board source instead of members here
/// — the same ray walk read through the pattern engine, which additionally lets the blocking test be any authored
/// value range rather than only "occupied". A document project adds its own query (one that needs a participant's
/// position, say) as a further <see cref="BoardQuery"/> case behind its own <see cref="OperandFamily"/>.</summary>
public enum BoardQueryKind : byte {
    /// <summary>The adjacent cell, or -1 at an edge.</summary>
    Neighbour,
    /// <summary>Minimum nonnegative entry cost, -1 if unreachable, -2 if the visit budget was exhausted.</summary>
    PathCost,
    /// <summary>The 64-bit cell-set mask of cells whose value lies in an inclusive range; bit c is cell ordinal c. A
    /// mask carries through the topology's point group with the <c>image</c>/<c>shift</c> expression ops.</summary>
    Mask,
    /// <summary>The least 64-bit fingerprint of the board's values over every point-group element: equal for two
    /// boards that are the same up to symmetry.</summary>
    Canonical,
    /// <summary>The cell reached by an arbitrary (dx, dz) grid step from the key cell, or -1.</summary>
    Offset,
    /// <summary>The size of the connected component of in-range cells containing the key cell, under a visit budget.</summary>
    Component,
    /// <summary>The count of distinct cells adjacent to that component whose value lies in a second range — a Go
    /// group's liberties — under the same budget.</summary>
    Liberties,
    /// <summary>The liberties a stone placed on the empty key cell would have: the distinct in-range cells adjacent to
    /// the group it would join, the key cell itself excluded — 0 with no capture is suicide.</summary>
    LibertiesAt,
    /// <summary>The count of adjacent groups a stone placed on the empty key cell would capture: those whose every
    /// liberty is the key cell.</summary>
    CapturesAt,
    /// <summary>Whether the key cell is attacked: walking each of a short authored direction list from the key
    /// cell, the first occupied cell in at least one of them carries a value within an inclusive range. A ray that
    /// hits an occupied cell outside the range is blocked (stops there, counts as a miss) — the same single-direction
    /// blocker walk a <c>$match:</c> board-ray facet performs, unioned over the authored directions and filtered to
    /// a value range, so a slider's reach at one square is one query instead of one rule per direction. It does not
    /// by itself know a piece's movement shape (the caller supplies the direction list and the range), and it says
    /// nothing about a non-sliding mover — that stays <see cref="Neighbour"/>/<see cref="Offset"/> composition,
    /// cheap enough not to need a primitive.</summary>
    Attacks,
    /// <summary>A query a document project declares — evaluated by its own case type, never by this library.</summary>
    Custom,
}

/// <summary>The abstract case-type base for a bounded board query — one sealed class per
/// <see cref="BoardQueryKind"/>, each carrying only the arguments its own evaluation reads. A class, for the same
/// reason <see cref="OperandFact"/>'s cases are: nothing at runtime compares two queries for equality or identity.</summary>
public abstract class BoardQuery {
    /// <summary>Initializes the query over its adjacency table.</summary>
    /// <param name="kind">The query operation.</param>
    /// <param name="topology">The immutable adjacency table.</param>
    protected BoardQuery(BoardQueryKind kind, CompiledTopology topology) {
        Kind = kind;
        Topology = topology;
    }

    /// <summary>Gets the query operation.</summary>
    public BoardQueryKind Kind { get; }
    /// <summary>Gets the immutable adjacency table.</summary>
    public CompiledTopology Topology { get; }

    /// <summary>Returns the conservative visit count one evaluation costs, beyond reading the board itself.</summary>
    public virtual long Visits => Topology.CellCount;
}

/// <summary>The adjacent cell in a topology-specific direction (<see cref="BoardQueryKind.Neighbour"/>) — also
/// the carrier for a <c>BoardShift</c>/<c>BoardImage</c> expression token and a pattern's board-ray source, both of
/// which read only <see cref="Direction"/> (a symmetry element ordinal for <c>BoardImage</c>, or -1 meaning "every
/// direction" for a pattern's <c>any</c> ray) without ever evaluating this query by its <see cref="BoardQuery.Kind"/>.</summary>
public sealed class BoardNeighbourQuery : BoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="direction">The topology-specific direction (or symmetry element) ordinal.</param>
    public BoardNeighbourQuery(CompiledTopology topology, int direction) : base(BoardQueryKind.Neighbour, topology) => Direction = direction;

    /// <summary>Gets the topology-specific direction (or symmetry element) ordinal.</summary>
    public int Direction { get; }
}

/// <summary>Minimum nonnegative entry cost to a target cell (<see cref="BoardQueryKind.PathCost"/>).</summary>
public sealed class BoardPathCostQuery : BoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="target">The path destination ordinal, read only when <paramref name="targetFrom"/> is
    /// <see langword="null"/>.</param>
    /// <param name="maxCost">The greatest admitted path cost.</param>
    /// <param name="maxVisits">The greatest settled nodes in one search.</param>
    /// <param name="targetFrom">A live indirection naming another declared row's cell whose integer value is the
    /// destination ordinal at evaluation time, or <see langword="null"/> for the compile-time literal
    /// <paramref name="target"/> — the same (row, key) cell-indirection every other dynamic key resolves through.</param>
    public BoardPathCostQuery(CompiledTopology topology, int target, long maxCost, int maxVisits, CompiledCellRef? targetFrom = null) : base(BoardQueryKind.PathCost, topology) {
        Target = target;
        MaxCost = maxCost;
        MaxVisits = maxVisits;
        TargetFrom = targetFrom;
    }

    /// <summary>Gets the path destination ordinal, when <see cref="TargetFrom"/> is <see langword="null"/>.</summary>
    public int Target { get; }
    /// <summary>Gets the greatest admitted path cost.</summary>
    public long MaxCost { get; }
    /// <summary>Gets the greatest settled nodes in one search.</summary>
    public int MaxVisits { get; }
    /// <summary>Gets the live indirection naming another declared row's cell whose integer value is the destination
    /// ordinal at evaluation time, or <see langword="null"/> for the compile-time literal <see cref="Target"/>.</summary>
    public CompiledCellRef? TargetFrom { get; }
    /// <inheritdoc/>
    public override long Visits => ((long)(MaxVisits + 1) * (Topology.CellCount + Topology.DirectionCount));
}

/// <summary>The connected component of cells whose value lies in an inclusive range, grown from the key cell along the
/// topology's directions under a visit budget (<see cref="BoardQueryKind.Component"/>), or the distinct cells adjacent
/// to that component whose value lies in a second inclusive range (<see cref="BoardQueryKind.Liberties"/>). A key cell
/// outside the range is a component of size zero with no liberties; a budget that runs out reads -2.</summary>
public sealed class BoardComponentQuery : BoardQuery {
    /// <summary>Initializes the query.</summary>
    /// <param name="topology">The compiled topology.</param>
    /// <param name="lower">The inclusive lower bound of a member's value.</param>
    /// <param name="upper">The inclusive upper bound of a member's value.</param>
    /// <param name="maxVisits">The most component cells the flood may settle, 1..CellCount.</param>
    /// <param name="libertyLower">The inclusive lower bound of a liberty's value, for <see cref="BoardQueryKind.Liberties"/>.</param>
    /// <param name="libertyUpper">The inclusive upper bound of a liberty's value.</param>
    /// <param name="liberties">Whether the query counts liberties rather than members.</param>
    public BoardComponentQuery(CompiledTopology topology, long lower, long upper, int maxVisits, long libertyLower, long libertyUpper, bool liberties)
        : this(topology, lower, upper, maxVisits, libertyLower, libertyUpper, liberties ? BoardQueryKind.Liberties : BoardQueryKind.Component) {
    }

    /// <summary>Initializes the query for any of its four kinds; the placement kinds (<see cref="BoardQueryKind.LibertiesAt"/>,
    /// <see cref="BoardQueryKind.CapturesAt"/>) read the key cell as the empty cell a stone would land on.</summary>
    /// <param name="topology">The compiled topology.</param>
    /// <param name="lower">The inclusive lower bound of a member's value.</param>
    /// <param name="upper">The inclusive upper bound of a member's value.</param>
    /// <param name="maxVisits">The most component cells the flood may settle, 1..CellCount.</param>
    /// <param name="libertyLower">The inclusive lower bound of a liberty's value.</param>
    /// <param name="libertyUpper">The inclusive upper bound of a liberty's value.</param>
    /// <param name="kind">The query kind.</param>
    public BoardComponentQuery(CompiledTopology topology, long lower, long upper, int maxVisits, long libertyLower, long libertyUpper, BoardQueryKind kind)
        : base(kind, topology) {
        Lower = lower;
        Upper = upper;
        MaxVisits = maxVisits;
        LibertyLower = libertyLower;
        LibertyUpper = libertyUpper;
    }

    /// <summary>Gets the inclusive lower bound of a member's value.</summary>
    public long Lower { get; }
    /// <summary>Gets the inclusive upper bound of a member's value.</summary>
    public long Upper { get; }
    /// <summary>Gets the most component cells the flood may settle.</summary>
    public int MaxVisits { get; }
    /// <summary>Gets the inclusive lower bound of a liberty's value.</summary>
    public long LibertyLower { get; }
    /// <summary>Gets the inclusive upper bound of a liberty's value.</summary>
    public long LibertyUpper { get; }
    /// <inheritdoc/>
    public override long Visits => (((long)(MaxVisits + 1) * Topology.DirectionCount) + Topology.CellCount);
}

/// <summary>The 64-bit cell-set mask of cells whose value lies in an inclusive range (<see cref="BoardQueryKind.Mask"/>).</summary>
public sealed class BoardMaskQuery : BoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="lower">The inclusive range lower bound.</param>
    /// <param name="upper">The inclusive range upper bound.</param>
    public BoardMaskQuery(CompiledTopology topology, long lower, long upper) : base(BoardQueryKind.Mask, topology) {
        Lower = lower;
        Upper = upper;
    }

    /// <summary>Gets the inclusive range lower bound.</summary>
    public long Lower { get; }
    /// <summary>Gets the inclusive range upper bound.</summary>
    public long Upper { get; }
}

/// <summary>The least 64-bit fingerprint of the board's values over every point-group element (<see cref="BoardQueryKind.Canonical"/>).</summary>
public sealed class BoardCanonicalQuery : BoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    public BoardCanonicalQuery(CompiledTopology topology) : base(BoardQueryKind.Canonical, topology) { }
    /// <inheritdoc/>
    public override long Visits => ((long)Topology.CellCount * Topology.ElementCount);
}

/// <summary>The cell reached by an arbitrary (dx, dz) grid step from the key cell (<see cref="BoardQueryKind.Offset"/>).</summary>
public sealed class BoardOffsetQuery : BoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="dx">The signed +X grid step.</param>
    /// <param name="dz">The signed +Z grid step.</param>
    public BoardOffsetQuery(CompiledTopology topology, int dx, int dz) : base(BoardQueryKind.Offset, topology) {
        Dx = dx;
        Dz = dz;
    }

    /// <summary>Gets the signed +X grid step.</summary>
    public int Dx { get; }
    /// <summary>Gets the signed +Z grid step.</summary>
    public int Dz { get; }
}

/// <summary>Whether the key cell is attacked along any of a short authored direction list (<see cref="BoardQueryKind.Attacks"/>).</summary>
public sealed class BoardAttacksQuery : BoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="lower">The inclusive range lower bound.</param>
    /// <param name="upper">The inclusive range upper bound.</param>
    /// <param name="directions">The 1..4 direction ordinals walked — a concrete array so the per-evaluation walk
    /// indexes it directly rather than boxing an interface enumerator.</param>
    public BoardAttacksQuery(CompiledTopology topology, long lower, long upper, int[] directions) : base(BoardQueryKind.Attacks, topology) {
        Lower = lower;
        Upper = upper;
        Directions = directions;
    }

    /// <summary>Gets the inclusive range lower bound.</summary>
    public long Lower { get; }
    /// <summary>Gets the inclusive range upper bound.</summary>
    public long Upper { get; }
    /// <summary>Gets the 1..4 direction ordinals walked.</summary>
    public int[] Directions { get; }
    /// <inheritdoc/>
    public override long Visits => ((long)Topology.CellCount * Directions.Length);
}

/// <summary>The most cells a board may hold for its occupancy to read as one 64-bit mask.</summary>
public static class BoardMask {
    /// <summary>Cell ordinals 0..63 map to bits 0..63.</summary>
    public const int MaxCells = 64;
}
