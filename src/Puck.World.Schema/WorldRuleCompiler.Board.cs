using System.Globalization;

namespace Puck.World;

/// <summary>The finite discrete-board query vocabulary. A ray's first-blocker cell, its distance, and a run-length
/// existence check are <c>$match:</c> facets over a board source instead of members here — the same ray walk read
/// through the pattern engine, which additionally lets the blocking test be any authored value range rather than
/// only "occupied".</summary>
public enum WorldBoardQueryKind : byte {
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
    /// <summary>The grid cell a referenced body's world position falls in, or -1.</summary>
    CellOf,
    /// <summary>The cell reached by an arbitrary (dx, dz) grid step from the key cell, or -1.</summary>
    Offset,
    /// <summary>Whether the key cell is attacked: walking each of a short authored direction list from the key
    /// cell, the first occupied cell in at least one of them carries a value within an inclusive range. A ray that
    /// hits an occupied cell outside the range is blocked (stops there, counts as a miss) — the same single-direction
    /// blocker walk a <c>$match:</c> board-ray facet performs, unioned over the authored directions and filtered to
    /// a value range, so a slider's reach at one square is one query instead of one rule per direction. It does not
    /// by itself know a piece's movement shape (the caller supplies the direction list and the range), and it says
    /// nothing about a non-sliding mover — that stays <see cref="Neighbour"/>/<see cref="Offset"/> composition,
    /// cheap enough not to need a primitive.</summary>
    Attacks,
}

/// <summary>The abstract case-type base for a bounded board query — one sealed class per
/// <see cref="WorldBoardQueryKind"/>, each carrying only the arguments its own evaluation reads. A CLASS, for the
/// same reason <see cref="WorldOperandFact"/>'s cases are: nothing at runtime compares two queries for equality or
/// identity. Already a reference type before this split (the record it replaces was never a record struct), so no
/// wrapping carrier is needed — a field simply holds this base type, or its nullable reference, and dispatch is a
/// type-pattern switch (see <c>WorldBoardQueries.Evaluate</c>) or the shared <see cref="Kind"/>/<see cref="Topology"/>
/// every case carries.</summary>
public abstract class CompiledWorldBoardQuery {
    private protected CompiledWorldBoardQuery(WorldBoardQueryKind kind, CompiledWorldTopology topology) {
        Kind = kind;
        Topology = topology;
    }

    /// <summary>The query operation.</summary>
    public WorldBoardQueryKind Kind { get; }
    /// <summary>The immutable adjacency table.</summary>
    public CompiledWorldTopology Topology { get; }
}

/// <summary>The adjacent cell in a topology-specific direction (<see cref="WorldBoardQueryKind.Neighbour"/>) — also
/// the carrier for a <c>BoardShift</c>/<c>BoardImage</c> expression token and a pattern's board-ray source, both of
/// which read only <see cref="Direction"/> (a symmetry element ordinal for <c>BoardImage</c>, or -1 meaning "every
/// direction" for a pattern's <c>any</c> ray) without ever evaluating this query by its <see cref="CompiledWorldBoardQuery.Kind"/>.</summary>
public sealed class BoardNeighbourQuery : CompiledWorldBoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="direction">The topology-specific direction (or symmetry element) ordinal.</param>
    public BoardNeighbourQuery(CompiledWorldTopology topology, int direction) : base(WorldBoardQueryKind.Neighbour, topology) => Direction = direction;

    /// <summary>The topology-specific direction (or symmetry element) ordinal.</summary>
    public int Direction { get; }
}

/// <summary>Minimum nonnegative entry cost to a target cell (<see cref="WorldBoardQueryKind.PathCost"/>).</summary>
public sealed class BoardPathCostQuery : CompiledWorldBoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="target">The path destination ordinal, read only when <paramref name="targetFrom"/> is
    /// <see langword="null"/>.</param>
    /// <param name="maxCost">The greatest admitted path cost.</param>
    /// <param name="maxVisits">The greatest settled nodes in one search.</param>
    /// <param name="targetFrom">A live indirection naming another declared row's cell whose integer value is the
    /// destination ordinal at evaluation time, or <see langword="null"/> for the compile-time literal
    /// <paramref name="target"/> — the same (row, key) cell-indirection every other dynamic key resolves through.</param>
    public BoardPathCostQuery(CompiledWorldTopology topology, int target, long maxCost, int maxVisits, CompiledCellRef? targetFrom = null) : base(WorldBoardQueryKind.PathCost, topology) {
        Target = target;
        MaxCost = maxCost;
        MaxVisits = maxVisits;
        TargetFrom = targetFrom;
    }

    /// <summary>The path destination ordinal, when <see cref="TargetFrom"/> is <see langword="null"/>.</summary>
    public int Target { get; }
    /// <summary>The greatest admitted path cost.</summary>
    public long MaxCost { get; }
    /// <summary>The greatest settled nodes in one search.</summary>
    public int MaxVisits { get; }
    /// <summary>A live indirection naming another declared row's cell whose integer value is the destination
    /// ordinal at evaluation time, or <see langword="null"/> for the compile-time literal <see cref="Target"/>.</summary>
    public CompiledCellRef? TargetFrom { get; }
}

/// <summary>The 64-bit cell-set mask of cells whose value lies in an inclusive range (<see cref="WorldBoardQueryKind.Mask"/>).</summary>
public sealed class BoardMaskQuery : CompiledWorldBoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="lower">The inclusive range lower bound.</param>
    /// <param name="upper">The inclusive range upper bound.</param>
    public BoardMaskQuery(CompiledWorldTopology topology, long lower, long upper) : base(WorldBoardQueryKind.Mask, topology) {
        Lower = lower;
        Upper = upper;
    }

    /// <summary>The inclusive range lower bound.</summary>
    public long Lower { get; }
    /// <summary>The inclusive range upper bound.</summary>
    public long Upper { get; }
}

/// <summary>The least 64-bit fingerprint of the board's values over every point-group element (<see cref="WorldBoardQueryKind.Canonical"/>).</summary>
public sealed class BoardCanonicalQuery : CompiledWorldBoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    public BoardCanonicalQuery(CompiledWorldTopology topology) : base(WorldBoardQueryKind.Canonical, topology) { }
}

/// <summary>The grid cell a referenced body's world position falls in (<see cref="WorldBoardQueryKind.CellOf"/>).</summary>
public sealed class BoardCellOfQuery : CompiledWorldBoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    public BoardCellOfQuery(CompiledWorldTopology topology) : base(WorldBoardQueryKind.CellOf, topology) { }
}

/// <summary>The cell reached by an arbitrary (dx, dz) grid step from the key cell (<see cref="WorldBoardQueryKind.Offset"/>).</summary>
public sealed class BoardOffsetQuery : CompiledWorldBoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="dx">The signed +X grid step.</param>
    /// <param name="dz">The signed +Z grid step.</param>
    public BoardOffsetQuery(CompiledWorldTopology topology, int dx, int dz) : base(WorldBoardQueryKind.Offset, topology) {
        Dx = dx;
        Dz = dz;
    }

    /// <summary>The signed +X grid step.</summary>
    public int Dx { get; }
    /// <summary>The signed +Z grid step.</summary>
    public int Dz { get; }
}

/// <summary>Whether the key cell is attacked along any of a short authored direction list (<see cref="WorldBoardQueryKind.Attacks"/>).</summary>
public sealed class BoardAttacksQuery : CompiledWorldBoardQuery {
    /// <param name="topology">The immutable adjacency table.</param>
    /// <param name="lower">The inclusive range lower bound.</param>
    /// <param name="upper">The inclusive range upper bound.</param>
    /// <param name="directions">The 1..4 direction ordinals walked — a concrete array so the per-evaluation walk
    /// indexes it directly rather than boxing an interface enumerator.</param>
    public BoardAttacksQuery(CompiledWorldTopology topology, long lower, long upper, int[] directions) : base(WorldBoardQueryKind.Attacks, topology) {
        Lower = lower;
        Upper = upper;
        Directions = directions;
    }

    /// <summary>The inclusive range lower bound.</summary>
    public long Lower { get; }
    /// <summary>The inclusive range upper bound.</summary>
    public long Upper { get; }
    /// <summary>The 1..4 direction ordinals walked.</summary>
    public int[] Directions { get; }
}

/// <summary>The most cells a board may hold for its occupancy to read as one 64-bit mask.</summary>
public static class WorldBoardMask {
    /// <summary>Cell ordinals 0..63 map to bits 0..63.</summary>
    public const int MaxCells = 64;
}

public static partial class WorldRuleCompiler {
    private static ResolvedOperand ResolveBoardOperand(string name, string? key, string ruleName, WorldDefinition definition) {
        WorldRuleException Invalid(string detail) => new(WorldRuleRefusal.StateCellUnaddressable, ruleName, detail);
        var tokens = name.Split(':');
        if (tokens.Length < 3 || (tokens.Length < 4 && tokens[1] != "canonical")) {
            throw Invalid("board query requires $board:<operation>:<row>:<arguments>");
        }
        var row = WorldDefinitionRows.FindStateRow(definition.State, tokens[2]);
        if (row?.Board is not { } board || WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology) {
            throw Invalid($"'{tokens[2]}' names no discrete board row");
        }
        var kind = tokens[1] switch {
            "neighbour" => WorldBoardQueryKind.Neighbour,
            "pathCost" => WorldBoardQueryKind.PathCost,
            "mask" => WorldBoardQueryKind.Mask,
            "canonical" => WorldBoardQueryKind.Canonical,
            "cellOf" => WorldBoardQueryKind.CellOf,
            "offset" => WorldBoardQueryKind.Offset,
            "attacks" => WorldBoardQueryKind.Attacks,
            _ => throw Invalid($"unknown board operation '{tokens[1]}'"),
        };
        if (kind is WorldBoardQueryKind.Mask && topology.CellCount > WorldBoardMask.MaxCells) {
            throw Invalid($"{tokens[1]} reads at most {WorldBoardMask.MaxCells} cells as bits; '{board.Topology}' has {topology.CellCount}");
        }
        if (kind is WorldBoardQueryKind.CellOf or WorldBoardQueryKind.Offset && topology.Kind != WorldTopologyKind.Grid) {
            throw Invalid($"'{tokens[1]}' requires a Grid topology, not {topology.Kind}");
        }
        CompiledCellRef? keyFrom = null;
        if (kind is not (WorldBoardQueryKind.Mask or WorldBoardQueryKind.Canonical or WorldBoardQueryKind.CellOf)) {
            if (TryResolveDynamicKey(definition: definition, key: key, ruleName: ruleName, verb: "board", keyFieldLabel: "key", cell: out var dynamicKey)) {
                keyFrom = dynamicKey;
            } else if (key is null || !topology.TryCell(key, out _)) {
                throw Invalid("board query key must name a source cell or use a validated dynamic key");
            }
        } else if (key is not null) {
            throw Invalid($"{tokens[1]} does not accept key");
        }
        CompiledWorldBoardQuery query;
        CompiledBodyRef? bodyRef = null;
        if (kind == WorldBoardQueryKind.Canonical) {
            if (tokens.Length != 3) {
                throw Invalid("canonical takes no arguments beyond the row");
            }
            query = new BoardCanonicalQuery(topology);
        } else if (kind == WorldBoardQueryKind.Mask) {
            if (tokens.Length != 5 || row.Kind is not (CellKind.Int or CellKind.Bool) ||
                !long.TryParse(tokens[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var lower) ||
                !long.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var upper) || lower > upper) {
                throw Invalid("mask requires <min>:<max> integer bounds, min <= max, on an integer or boolean board row");
            }
            query = new BoardMaskQuery(topology, lower, upper);
        } else if (kind == WorldBoardQueryKind.PathCost) {
            if (row.Kind != CellKind.Int) {
                throw Invalid("pathCost requires <targetCell>:<maxCost>:<maxVisits> or cell:<row>:<key>:<maxCost>:<maxVisits> on an integer terrain row");
            }

            // A dynamic target — 'cell:<row>:<key>', the same (row, key) cell-indirection $distance:/$los:'s own
            // body-reference grammar spends on 'cell:<row>:<key>' — reads its live integer value as the destination
            // ordinal every evaluation, instead of the literal ordinal $board:pathCost: took at compile time alone.
            var dynamicTarget = ((tokens.Length == 8) && string.Equals(tokens[3], "cell", StringComparison.Ordinal));
            var target = 0;
            CompiledCellRef? targetFrom = null;
            var argumentsStart = 4;

            if (dynamicTarget) {
                targetFrom = ResolveCellRef(channel: name, definition: definition, key: tokens[5], row: tokens[4], ruleName: ruleName);
                argumentsStart = 6;
            } else if (tokens.Length != 6 || !topology.TryCell(tokens[3], out target)) {
                throw Invalid("pathCost requires <targetCell>:<maxCost>:<maxVisits> or cell:<row>:<key>:<maxCost>:<maxVisits> on an integer terrain row");
            }

            if (!long.TryParse(tokens[argumentsStart], NumberStyles.None, CultureInfo.InvariantCulture, out var cost) || cost < 0 ||
                !int.TryParse(tokens[argumentsStart + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var visits) || visits < 1 || visits > topology.CellCount) {
                throw Invalid("pathCost requires <targetCell>:<maxCost>:<maxVisits> or cell:<row>:<key>:<maxCost>:<maxVisits> on an integer terrain row");
            }
            query = new BoardPathCostQuery(topology, target, cost, visits, targetFrom);
        } else if (kind == WorldBoardQueryKind.Attacks) {
            if (tokens.Length != 6 || row.Kind is not (CellKind.Int or CellKind.Bool) ||
                !long.TryParse(tokens[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var attackLower) ||
                !long.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var attackUpper) || attackLower > attackUpper) {
                throw Invalid("attacks requires <min>:<max> integer bounds, min <= max, on an integer or boolean board row");
            }
            var directionNames = tokens[5].Split(',');
            if (directionNames.Length is < 1 or > 4) {
                throw Invalid("attacks requires 1..4 comma-separated directions");
            }
            var directions = new int[directionNames.Length];
            for (var index = 0; index < directionNames.Length; index++) {
                var directionOrdinal = topology.Direction(directionNames[index]);
                if (directionOrdinal < 0) {
                    throw Invalid($"'{directionNames[index]}' is not a direction valid for its topology");
                }
                directions[index] = directionOrdinal;
            }
            query = new BoardAttacksQuery(topology, attackLower, attackUpper, directions);
        } else if (kind == WorldBoardQueryKind.Offset) {
            if (tokens.Length != 5 || !int.TryParse(tokens[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dx) ||
                !int.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dz)) {
                throw Invalid("offset requires <dx>:<dz>, both signed integers");
            }
            query = new BoardOffsetQuery(topology, dx, dz);
        } else if (kind == WorldBoardQueryKind.CellOf) {
            var bodyTokens = tokens[3..];
            var width = BodyRefTokenWidth(start: 0, tokens: bodyTokens);
            if (bodyTokens.Length != width) {
                throw Invalid($"'{name}' does not spell '$board:cellOf:<row>:<bodyRef>' ({s_bodyRefVocabulary})");
            }
            bodyRef = ResolveBodyRefToken(channel: name, definition: definition, ruleName: ruleName, start: 0, tokens: bodyTokens);
            query = new BoardCellOfQuery(topology);
        } else {
            if (tokens.Length != 4) {
                throw Invalid("neighbour requires exactly one direction token");
            }
            var direction = topology.Direction(tokens[3]);
            if (direction < 0) {
                throw Invalid($"'{tokens[3]}' is not a direction of '{board.Topology}'");
            }
            query = new BoardNeighbourQuery(topology, direction);
        }
        return new(new CompiledWorldOperand(new BoardOperand(
            row: row.Name,
            key: key,
            keyFrom: keyFrom,
            stateHandle: ResolveWorldStateHandle(definition: definition, name: row.Name),
            board: query,
            bodyA: bodyRef
        )), CellKind.Int, name);
    }
}
