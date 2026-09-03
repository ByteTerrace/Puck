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
public sealed record CompiledWorldBoardQuery(CompiledWorldTopology Topology, WorldBoardQueryKind Kind,
    int Direction = 0, int Length = 0, long Value = 0, bool Exact = false, int Target = 0, long MaxCost = 0, int MaxVisits = 0);

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
            _ => throw Invalid($"unknown board operation '{tokens[1]}'"),
        };
        CompiledCellRef? keyFrom = null;
        if (kind != WorldBoardQueryKind.Line) {
            if (TryResolveDynamicKey(definition: definition, key: key, ruleName: ruleName, verb: "board", keyFieldLabel: "key", cell: out var dynamicKey)) {
                keyFrom = dynamicKey;
            } else if (key is null || !topology.TryCell(key, out _)) {
                throw Invalid("board query key must name a source cell or use a validated dynamic key");
            }
        } else if (key is not null) {
            throw Invalid("line searches the whole board and does not accept key");
        }
        var query = new CompiledWorldBoardQuery(topology, kind);
        if (kind == WorldBoardQueryKind.Line) {
            if (tokens.Length != 6 || !int.TryParse(tokens[3], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 1 || length > topology.CellCount || !long.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
                value < WorldStateCapacity.MinIntCellValue || value > WorldStateCapacity.MaxIntCellValue ||
                tokens[5] is not ("exact" or "atLeast")) {
                throw Invalid("line requires <length>:<integerValue>:<exact|atLeast>");
            }
            query = query with { Length = length, Value = value, Exact = tokens[5] == "exact" };
        } else if (kind == WorldBoardQueryKind.PathCost) {
            if (tokens.Length != 6 || row.Kind != CellKind.Int || !topology.TryCell(tokens[3], out var target) ||
                !long.TryParse(tokens[4], NumberStyles.None, CultureInfo.InvariantCulture, out var cost) || cost < 0 || cost > WorldStateCapacity.MaxIntCellValue ||
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
