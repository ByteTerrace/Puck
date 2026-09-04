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
/// <param name="Upper">The inclusive upper bound of a <see cref="WorldBoardQueryKind.Mask"/> range; <paramref name="Value"/> is its lower bound.</param>
public sealed record CompiledWorldBoardQuery(CompiledWorldTopology Topology, WorldBoardQueryKind Kind,
    int Direction = 0, int Length = 0, long Value = 0, bool Exact = false, int Target = 0, long MaxCost = 0, int MaxVisits = 0,
    long Upper = 0);

/// <summary>The most cells a board may hold for its occupancy to read as one 64-bit mask.</summary>
public static class WorldBoardMask {
    /// <summary>Cell ordinals 0..63 map to bits 0..63.</summary>
    public const int MaxCells = 64;
}

public static partial class WorldRuleCompiler {
    private static ResolvedOperand ResolveBoardOperand(string name, string? key, string ruleName, WorldDefinition definition) {
        WorldRuleException Invalid(string detail) => new(WorldRuleRefusal.StateCellUnaddressable, ruleName, detail);
        var tokens = name.Split(':');
        if (tokens.Length < 4) {
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
            _ => throw Invalid($"unknown board operation '{tokens[1]}'"),
        };
        if (kind == WorldBoardQueryKind.Mask && topology.CellCount > WorldBoardMask.MaxCells) {
            throw Invalid($"mask reads at most {WorldBoardMask.MaxCells} cells as bits; '{board.Topology}' has {topology.CellCount}");
        }
        CompiledCellRef? keyFrom = null;
        if (kind is not (WorldBoardQueryKind.Line or WorldBoardQueryKind.Mask)) {
            if (TryResolveDynamicKey(definition: definition, key: key, ruleName: ruleName, verb: "board", keyFieldLabel: "key", cell: out var dynamicKey)) {
                keyFrom = dynamicKey;
            } else if (key is null || !topology.TryCell(key, out _)) {
                throw Invalid("board query key must name a source cell or use a validated dynamic key");
            }
        } else if (key is not null) {
            throw Invalid($"{tokens[1]} reads the whole board and does not accept key");
        }
        var query = new CompiledWorldBoardQuery(topology, kind);
        if (kind == WorldBoardQueryKind.Line) {
            if (tokens.Length != 6 || !int.TryParse(tokens[3], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 1 || length > topology.CellCount || !long.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
                tokens[5] is not ("exact" or "atLeast")) {
                throw Invalid("line requires <length>:<integerValue>:<exact|atLeast>");
            }
            query = query with { Length = length, Value = value, Exact = tokens[5] == "exact" };
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
        } else {
            var direction = topology.Direction(tokens[3]);
            if (tokens.Length != 4 || direction < 0) {
                throw Invalid("board ray/neighbour requires one direction valid for its topology");
            }
            query = query with { Direction = direction };
        }
        return new(new CompiledWorldOperand(WorldRuleFactKind.Board, row.Name, key, KeyFrom: keyFrom, ValueKind: CellKind.Int, Board: query), CellKind.Int, name);
    }
}
