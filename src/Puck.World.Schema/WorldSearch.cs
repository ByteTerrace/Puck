using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The <c>search</c> section: jobs that ask what the board would be. A job relocates each token of a keyed
/// row to every other cell of a board in turn, evaluates the document's own rules over a value frame holding that
/// hypothetical position, and records which relocations the rules accepted — the verdict row at its accept value and
/// the turn row changed. The job spends a node quota per tick and lands its answer as ordinary state writes when it
/// finishes; it restarts whenever any framed cell other than its own outputs changes.</summary>
/// <param name="Jobs">The declared jobs.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSearchSection(IReadOnlyList<WorldSearchRow>? Jobs = null) {
    /// <summary>Gets a section declaring no job.</summary>
    public static WorldSearchSection Absent { get; } = new();
    /// <summary>Gets the declared jobs.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldSearchRow> Rows => (Jobs ?? []);
}

/// <summary>One search job.</summary>
/// <param name="Name">The stable job name.</param>
/// <param name="Tokens">The keyed integer row whose cells are the tokens and whose values are the board cells they
/// stand on; a value that is no cell is a token off the board, which the job leaves alone.</param>
/// <param name="Board">The board row over the topology the token values index.</param>
/// <param name="Turn">The slot row whose change marks an accepted relocation; absent, the tabletop board binding
/// anchoring <paramref name="Board"/> supplies it.</param>
/// <param name="Verdict">The slot row the rules judge a relocation into; absent, the same board binding supplies it.</param>
/// <param name="Accept">The verdict value that accepts a relocation.</param>
/// <param name="Legal">An integer row keyed by the tokens receiving, per token, the mask of cells it may relocate to;
/// requires a board of at most 64 cells.</param>
/// <param name="Count">A slot row receiving how many relocations were accepted.</param>
/// <param name="Nodes">The relocations judged per tick, at most what the work sheet leaves; absent derives that.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSearchRow(
    string Name,
    string Tokens,
    string Board,
    string? Turn = null,
    string? Verdict = null,
    long Accept = 1L,
    string? Legal = null,
    string? Count = null,
    int? Nodes = null
);

/// <summary>Hard bounds for the search section.</summary>
public static class WorldSearchCapacity {
    /// <summary>The most jobs one document declares.</summary>
    public const int MaxJobs = 8;
    /// <summary>The most relocations one job judges per tick, whatever the work sheet leaves.</summary>
    public const int MaxNodesPerTick = 4_096;
}

/// <summary>One job's derived plan: every row resolved, the off-board value, and the per-tick node quota.</summary>
/// <param name="Row">The authored job.</param>
/// <param name="Topology">The board's topology.</param>
/// <param name="Turn">The turn row.</param>
/// <param name="Verdict">The verdict row.</param>
/// <param name="Off">The token value meaning off the board.</param>
/// <param name="Nodes">The relocations judged per tick.</param>
/// <param name="JudgeCost">The work units one judge run costs.</param>
public sealed record WorldSearchPlan(WorldSearchRow Row, CompiledTopology Topology, string Turn, string Verdict, long Off, int Nodes, long JudgeCost);

/// <summary>Derives what a search job needs from the document: the rules a frame can evaluate, their cost, and each
/// job's plan.</summary>
public static class WorldSearchCompilation {
    /// <summary>Returns the rules a frame evaluates: every rule that is neither an interaction nor a decision and
    /// reads no fact only the world host answers.</summary>
    /// <param name="rules">The compiled rules.</param>
    public static CompiledWorldRule[] JudgeRules(CompiledWorldRule[] rules) {
        ArgumentNullException.ThrowIfNull(argument: rules);

        var judge = new List<CompiledWorldRule>(capacity: rules.Length);

        foreach (var rule in rules) {
            if ((rule.Interaction is null) && (rule.Decision is null) && !RuleDataflow.ReadsHost(rule: rule)) {
                judge.Add(item: rule);
            }
        }

        return judge.ToArray();
    }

    /// <summary>Returns the work units one judge run costs: every judge rule's evaluations times its unit cost.</summary>
    /// <param name="judge">The judge rules.</param>
    /// <param name="context">The compile context.</param>
    public static long JudgeCost(CompiledWorldRule[] judge, WorldRuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: judge);
        ArgumentNullException.ThrowIfNull(argument: context);

        var cost = 0L;

        foreach (var rule in judge) {
            var multiplier = ((rule.ForEach is { } rowName) ? context.RowCapacity(name: rowName) : 1L);

            cost = RuleWorkBudget.SaturatingAdd(left: cost, right: RuleWorkBudget.Contributor(rule: rule, multiplier: multiplier, isInteraction: false, context: context).WorkUnits);
        }

        return Math.Max(val1: 1L, val2: cost);
    }

    /// <summary>Derives one job's plan, or names why the job cannot run.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="row">The job.</param>
    /// <param name="judgeCost">The work units one judge run costs.</param>
    /// <param name="leftover">The work units the sheet leaves per tick, shared by every job.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="reason">Why the job cannot run, or empty.</param>
    public static bool TryPlan(WorldDefinition definition, WorldSearchRow row, long judgeCost, long leftover, out WorldSearchPlan? plan, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: row);
        plan = null;

        var tokens = WorldDefinitionRows.FindStateRow(rows: definition.State, name: row.Tokens);
        var board = WorldDefinitionRows.FindStateRow(rows: definition.State, name: row.Board);

        if (tokens is not { IsKeyed: true, Kind: CellKind.Int } || (tokens.EffectiveDomain is StateDomain.CellsOf or StateDomain.Ring)) {
            reason = $"search '{row.Name}' tokens '{row.Tokens}' must be a keyed integer row";

            return false;
        }
        if ((board?.EffectiveDomain is not StateDomain.CellsOf cells) || (board.Kind != CellKind.Int) || (WorldTopologyCompilation.Find(definition, cells.Topology) is not { } topology) || (topology.Kind == TopologyKind.Field)) {
            reason = $"search '{row.Name}' board '{row.Board}' must be an integer board over a discrete topology";

            return false;
        }

        WorldPlacementBoard? binding = null;

        foreach (var placement in definition.Placements) {
            if ((placement.Board is { } facet) && string.Equals(a: facet.Occupancy, b: row.Board, comparisonType: StringComparison.Ordinal)) {
                binding = facet;

                break;
            }
        }

        var turnName = (row.Turn ?? binding?.Turn);
        var verdictName = (row.Verdict ?? binding?.Verdict);

        if ((turnName is null) || (verdictName is null)) {
            reason = $"search '{row.Name}' names no turn or verdict row and no tabletop board binding over '{row.Board}' supplies them";

            return false;
        }
        if (WorldDefinitionRows.FindStateRow(rows: definition.State, name: turnName) is not { IsSlot: true, Kind: CellKind.Int }) {
            reason = $"search '{row.Name}' turn '{turnName}' must be an integer slot row";

            return false;
        }
        if (WorldDefinitionRows.FindStateRow(rows: definition.State, name: verdictName) is not { IsSlot: true, Kind: CellKind.Int }) {
            reason = $"search '{row.Name}' verdict '{verdictName}' must be an integer slot row";

            return false;
        }
        if (row.Legal is { } legalName) {
            if (WorldDefinitionRows.FindStateRow(rows: definition.State, name: legalName) is not { Kind: CellKind.Int, EffectiveDomain: StateDomain.KeysOf keysOf } || !string.Equals(a: keysOf.Row.Value, b: row.Tokens, comparisonType: StringComparison.Ordinal)) {
                reason = $"search '{row.Name}' legal '{legalName}' must be an integer row keyed by '{row.Tokens}'";

                return false;
            }
            if (topology.CellCount > BoardMask.MaxCells) {
                reason = $"search '{row.Name}' legal masks need a board of at most {BoardMask.MaxCells} cells; '{cells.Topology}' has {topology.CellCount}";

                return false;
            }
        }
        if ((row.Count is { } countName) && (WorldDefinitionRows.FindStateRow(rows: definition.State, name: countName) is not { IsSlot: true, Kind: CellKind.Int })) {
            reason = $"search '{row.Name}' count '{countName}' must be an integer slot row";

            return false;
        }

        var off = (tokens.Min ?? -1L);

        if ((off >= 0L) && (off < topology.CellCount)) {
            reason = $"search '{row.Name}' tokens '{row.Tokens}' declares min {off}, a cell of the board; a token off the board needs a value that is no cell";

            return false;
        }

        var derived = (int)Math.Min(val1: (leftover / judgeCost), val2: WorldSearchCapacity.MaxNodesPerTick);

        if (derived < 1) {
            reason = $"search '{row.Name}' has no work left: the rules leave {leftover} work units per tick and one judge run costs {judgeCost}";

            return false;
        }
        if (row.Nodes is { } authored && ((authored < 1) || (authored > derived))) {
            reason = $"search '{row.Name}' nodes {authored} must lie in 1..{derived}, what the work sheet leaves";

            return false;
        }

        plan = new WorldSearchPlan(Row: row, Topology: topology, Turn: turnName, Verdict: verdictName, Off: off, Nodes: (row.Nodes ?? derived), JudgeCost: judgeCost);
        reason = string.Empty;

        return true;
    }

    /// <summary>Derives every job's plan against the compiled rules, or names the first job that cannot run.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="rules">The compiled rules.</param>
    /// <param name="plans">The plans, in section order.</param>
    /// <param name="judge">The rules a frame evaluates.</param>
    /// <param name="reason">Why a job cannot run, or empty.</param>
    public static bool TryPlanAll(WorldDefinition definition, CompiledWorldRule[] rules, out WorldSearchPlan[] plans, out CompiledWorldRule[] judge, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: rules);

        var rows = definition.Search.Rows;
        judge = JudgeRules(rules: rules);
        plans = new WorldSearchPlan[rows.Count];

        if (rows.Count == 0) {
            reason = string.Empty;

            return true;
        }

        var context = WorldRuleCompiler.Context(definition: definition);
        var judgeCost = JudgeCost(judge: judge, context: context);
        var sheet = WorldRuleWorkBudget.Measure(definition: definition).WorkUnitsPerTick;
        var leftover = (Math.Max(val1: 0L, val2: (RuleCapacity.MaxWorkUnitsPerTick - sheet)) / rows.Count);

        for (var index = 0; index < rows.Count; index++) {
            if (!TryPlan(definition: definition, row: rows[index], judgeCost: judgeCost, leftover: leftover, plan: out var plan, reason: out reason)) {
                return false;
            }

            plans[index] = plan!;
        }

        reason = string.Empty;

        return true;
    }
}
