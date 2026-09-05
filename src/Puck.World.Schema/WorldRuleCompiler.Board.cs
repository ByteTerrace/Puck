using System.Globalization;

namespace Puck.World;

/// <summary>The finite discrete-board query vocabulary.</summary>
public enum WorldBoardQueryKind : byte {
    /// <summary>The adjacent cell, or -1 at an edge.</summary>
    Neighbour,
    /// <summary>The first occupied ray cell, or -1.</summary>
    RayCell,
    /// <summary>The distance to the first occupied ray cell, or -1.</summary>
    RayDistance,
    /// <summary>Whether a qualifying line exists.</summary>
    Line,
    /// <summary>Minimum nonnegative entry cost, -1 if unreachable, -2 if the visit budget was exhausted.</summary>
    PathCost,
    /// <summary>The 64-bit mask of cells whose value lies in an inclusive range; bit c is cell ordinal c.</summary>
    Mask,
    /// <summary>The mask of a range's cells carried through one point-group element.</summary>
    Image,
    /// <summary>The least 64-bit fingerprint of the board's values over every point-group element: equal for two
    /// boards that are the same up to symmetry.</summary>
    Canonical,
    /// <summary>The least image mask of a range over every point-group element.</summary>
    CanonicalMask,
    /// <summary>The grid cell a referenced body's world position falls in, or -1.</summary>
    CellOf,
    /// <summary>The cell reached by an arbitrary (dx, dz) grid step from the key cell, or -1.</summary>
    Offset,
    /// <summary>Whether the key cell is attacked: walking each of a short authored direction list from the key
    /// cell, the first occupied cell in at least one of them carries a value within an inclusive range. A ray that
    /// hits an occupied cell outside the range is blocked (stops there, counts as a miss) exactly like
    /// <see cref="RayCell"/> — this is <see cref="RayCell"/>'s single-direction blocker test, unioned over the
    /// authored directions and filtered to a value range, so a slider's reach at one square is one query instead of
    /// one rule per direction. It does not by itself know a piece's movement shape (the caller supplies the
    /// direction list and the range), and it says nothing about non-sliding attackers (king/knight/pawn) — those
    /// stay <see cref="Neighbour"/>/<see cref="Offset"/> composition, cheap enough not to need a primitive.</summary>
    Attacks,
}

/// <summary>A bounded board query with all structural arguments compiled once.</summary>
/// <param name="Topology">The immutable adjacency table.</param>
/// <param name="Kind">The query operation.</param>
/// <param name="Direction">The topology-specific direction ordinal.</param>
/// <param name="Length">The requested line length.</param>
/// <param name="Value">The raw line-match value.</param>
/// <param name="Exact">Whether longer runs are excluded.</param>
/// <param name="Target">The path destination ordinal.</param>
/// <param name="MaxCost">The greatest admitted path cost.</param>
/// <param name="MaxVisits">The greatest settled nodes in one search.</param>
/// <param name="Dx">The signed +X grid step for <see cref="WorldBoardQueryKind.Offset"/>.</param>
/// <param name="Dz">The signed +Z grid step for <see cref="WorldBoardQueryKind.Offset"/>.</param>
/// <param name="Upper">The inclusive upper bound of a <see cref="WorldBoardQueryKind.Mask"/> or
/// <see cref="WorldBoardQueryKind.Attacks"/> range; <paramref name="Value"/> is its lower bound.</param>
/// <param name="Directions">The 1..4 direction ordinals an <see cref="WorldBoardQueryKind.Attacks"/> query walks —
/// a concrete array so the per-evaluation walk indexes it directly rather than boxing an interface enumerator.</param>
public sealed record CompiledWorldBoardQuery(CompiledWorldTopology Topology, WorldBoardQueryKind Kind,
    int Direction = 0, int Length = 0, long Value = 0, bool Exact = false, int Target = 0, long MaxCost = 0, int MaxVisits = 0,
    int Dx = 0, int Dz = 0, long Upper = 0, int[]? Directions = null);

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
            "rayCell" => WorldBoardQueryKind.RayCell,
            "rayDistance" => WorldBoardQueryKind.RayDistance,
            "line" => WorldBoardQueryKind.Line,
            "pathCost" => WorldBoardQueryKind.PathCost,
            "mask" => WorldBoardQueryKind.Mask,
            "image" => WorldBoardQueryKind.Image,
            "canonical" => WorldBoardQueryKind.Canonical,
            "canonicalMask" => WorldBoardQueryKind.CanonicalMask,
            "cellOf" => WorldBoardQueryKind.CellOf,
            "offset" => WorldBoardQueryKind.Offset,
            "attacks" => WorldBoardQueryKind.Attacks,
            _ => throw Invalid($"unknown board operation '{tokens[1]}'"),
        };
        if (kind is WorldBoardQueryKind.Mask or WorldBoardQueryKind.Image or WorldBoardQueryKind.CanonicalMask && topology.CellCount > WorldBoardMask.MaxCells) {
            throw Invalid($"{tokens[1]} reads at most {WorldBoardMask.MaxCells} cells as bits; '{board.Topology}' has {topology.CellCount}");
        }
        if (kind is WorldBoardQueryKind.CellOf or WorldBoardQueryKind.Offset && topology.Kind != WorldTopologyKind.Grid) {
            throw Invalid($"'{tokens[1]}' requires a Grid topology, not {topology.Kind}");
        }
        CompiledCellRef? keyFrom = null;
        if (kind is not (WorldBoardQueryKind.Line or WorldBoardQueryKind.Mask or WorldBoardQueryKind.Image or WorldBoardQueryKind.Canonical or WorldBoardQueryKind.CanonicalMask or WorldBoardQueryKind.CellOf)) {
            if (TryResolveDynamicKey(definition: definition, key: key, ruleName: ruleName, verb: "board", keyFieldLabel: "key", cell: out var dynamicKey)) {
                keyFrom = dynamicKey;
            } else if (key is null || !topology.TryCell(key, out _)) {
                throw Invalid("board query key must name a source cell or use a validated dynamic key");
            }
        } else if (key is not null) {
            throw Invalid($"{tokens[1]} does not accept key");
        }
        var query = new CompiledWorldBoardQuery(topology, kind);
        CompiledBodyRef? bodyRef = null;
        if (kind == WorldBoardQueryKind.Line) {
            if (tokens.Length != 6 || !int.TryParse(tokens[3], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 1 || length > topology.CellCount || !long.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
                tokens[5] is not ("exact" or "atLeast")) {
                throw Invalid("line requires <length>:<integerValue>:<exact|atLeast>");
            }
            query = query with { Length = length, Value = value, Exact = tokens[5] == "exact" };
        } else if (kind == WorldBoardQueryKind.Canonical) {
            if (tokens.Length != 3) {
                throw Invalid("canonical takes no arguments beyond the row");
            }
        } else if (kind is WorldBoardQueryKind.Image or WorldBoardQueryKind.CanonicalMask) {
            var boundsAt = (kind == WorldBoardQueryKind.Image) ? 4 : 3;
            if (tokens.Length != boundsAt + 2 || row.Kind is not (CellKind.Int or CellKind.Bool) ||
                !long.TryParse(tokens[boundsAt], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var low) ||
                !long.TryParse(tokens[boundsAt + 1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var high) || low > high) {
                throw Invalid($"{tokens[1]} requires {((kind == WorldBoardQueryKind.Image) ? "<element>:" : string.Empty)}<min>:<max> integer bounds, min <= max, on an integer or boolean board row");
            }
            var element = (kind == WorldBoardQueryKind.Image) ? topology.Element(tokens[3]) : 0;
            if (element < 0) {
                throw Invalid($"'{tokens[3]}' is not a symmetry element of '{board.Topology}'");
            }
            query = query with { Direction = element, Value = low, Upper = high };
        } else if (kind == WorldBoardQueryKind.Mask) {
            if (tokens.Length != 5 || row.Kind is not (CellKind.Int or CellKind.Bool) ||
                !long.TryParse(tokens[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var lower) ||
                !long.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var upper) || lower > upper) {
                throw Invalid("mask requires <min>:<max> integer bounds, min <= max, on an integer or boolean board row");
            }
            query = query with { Value = lower, Upper = upper };
        } else if (kind == WorldBoardQueryKind.PathCost) {
            if (tokens.Length != 6 || row.Kind != CellKind.Int || !topology.TryCell(tokens[3], out var target) ||
                !long.TryParse(tokens[4], NumberStyles.None, CultureInfo.InvariantCulture, out var cost) || cost < 0 ||
                !int.TryParse(tokens[5], NumberStyles.None, CultureInfo.InvariantCulture, out var visits) || visits < 1 || visits > topology.CellCount) {
                throw Invalid("pathCost requires <targetCell>:<maxCost>:<maxVisits> on an integer terrain row");
            }
            query = query with { Target = target, MaxCost = cost, MaxVisits = visits };
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
            query = query with { Value = attackLower, Upper = attackUpper, Directions = directions };
        } else if (kind == WorldBoardQueryKind.Offset) {
            if (tokens.Length != 5 || !int.TryParse(tokens[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dx) ||
                !int.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dz)) {
                throw Invalid("offset requires <dx>:<dz>, both signed integers");
            }
            query = query with { Dx = dx, Dz = dz };
        } else if (kind == WorldBoardQueryKind.CellOf) {
            var bodyTokens = tokens[3..];
            var width = BodyRefTokenWidth(start: 0, tokens: bodyTokens);
            if (bodyTokens.Length != width) {
                throw Invalid($"'{name}' does not spell '$board:cellOf:<row>:<bodyRef>' ({s_bodyRefVocabulary})");
            }
            bodyRef = ResolveBodyRefToken(channel: name, definition: definition, ruleName: ruleName, start: 0, tokens: bodyTokens);
        } else {
            var direction = topology.Direction(tokens[3]);
            if (tokens.Length != 4 || direction < 0) {
                throw Invalid("board ray/neighbour requires one direction valid for its topology");
            }
            query = query with { Direction = direction };
        }
        return new(new CompiledWorldOperand(WorldRuleFactKind.Board, row.Name, key, KeyFrom: keyFrom, ValueKind: CellKind.Int, Board: query, BodyA: bodyRef, StateHandle: ResolveWorldStateHandle(definition: definition, name: row.Name)), CellKind.Int, name);
    }
}
