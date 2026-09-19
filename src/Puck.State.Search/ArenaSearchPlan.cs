using System.Diagnostics.CodeAnalysis;

namespace Puck.State;

/// <summary>One search job's baked chance node addressed against a <see cref="StateCatalog"/>: the ply whose move
/// choice the job averages over instead of choosing, and the outcome table a caller bakes once from the row's own
/// declared generator.</summary>
/// <param name="RowOrdinal">The row a chosen outcome writes.</param>
/// <param name="AtDepth">Negamax: the absolute ply (0 = root) whose move choice is replaced. Tree: the 1-based
/// playout ply at which the playout draws instead of choosing a candidate.</param>
/// <param name="CellCount">How many cells the row has, in its own cell order.</param>
/// <param name="Outcomes">Every outcome's per-cell values, flattened outcome-major
/// (<c>outcome * CellCount + cell</c>).</param>
/// <param name="Weights">Every outcome's relative weight, one per stride of <paramref name="Outcomes"/>.</param>
public sealed record ArenaSearchChancePlan(int RowOrdinal, int AtDepth, int CellCount, long[] Outcomes, ulong[] Weights);
/// <summary>One search job's plan addressed against a <see cref="StateCatalog"/>: every row it reads or writes is a
/// catalog ordinal and every cell a <see cref="CellKey"/>, so the walk resolves no name while it runs. The candidate
/// shapes stay <see cref="SearchShapePlan"/>s, whose enumeration order and per-shape candidate count the walk reads
/// unchanged; <paramref name="CodeOrdinals"/> carries each shape's resolved codes row, one entry per shape, at
/// <c>-1</c> for a shape that promotes nothing.</summary>
/// <param name="Name">The stable job name, for its status and refusals.</param>
/// <param name="TokensOrdinal">The keyed row whose cells are the tokens and whose values are the cells they stand
/// on.</param>
/// <param name="Topology">The board's topology, or <see langword="null"/> for a job over
/// <paramref name="ZoneOrdinals"/>.</param>
/// <param name="ZoneOrdinals">The ordered zone rows, in authored order; empty for a board job.</param>
/// <param name="CellCount">How many cells the job has: the board's, or the zone count.</param>
/// <param name="TurnOrdinal">The slot row whose change marks an accepted relocation.</param>
/// <param name="VerdictOrdinal">The slot row the judge writes its verdict into.</param>
/// <param name="Off">The token value meaning off the board.</param>
/// <param name="Nodes">The candidates judged per step.</param>
/// <param name="JudgeCost">The work units one judge run costs.</param>
/// <param name="Depth">How many plies the job searches ahead.</param>
/// <param name="Scored">Whether the job compares plies by a score; a scored job's judge reads one.</param>
/// <param name="Shapes">The candidate shapes, in declared order.</param>
/// <param name="CodeOrdinals">Each shape's codes row ordinal, or <c>-1</c>.</param>
/// <param name="BestOrdinal">The best-move output row, or <c>-1</c>.</param>
/// <param name="LegalOrdinal">The row keyed by <paramref name="TokensOrdinal"/> receiving each token's
/// accepted-cell bitmask, or <c>-1</c>.</param>
/// <param name="ReachOrdinal">The board row painted with the held token's accepted destinations, or
/// <c>-1</c>.</param>
/// <param name="HeldOrdinal">The slot row naming the token the reach output paints for, or <c>-1</c>.</param>
/// <param name="CountsOrdinal">The row keyed by <paramref name="TokensOrdinal"/> receiving each token's
/// accepted-candidate count, or <c>-1</c>.</param>
/// <param name="Accept">The verdict value that accepts a candidate.</param>
/// <param name="Method">How the job compares plies by its score.</param>
/// <param name="Iterations">The tree iterations a <see cref="SearchMethod.Tree"/> job runs.</param>
/// <param name="Chance">The job's baked chance node, or <see langword="null"/> for a job with none.</param>
/// <param name="ScoresOrdinal">The keyed row holding one score per seat, in the turn row's own ordinal order, or
/// <c>-1</c>; a level maximizes the mover seat's own entry rather than negating the reply.</param>
/// <param name="DrawSeed">The draw stream a <see cref="SearchMethod.Tree"/> job's playouts advance from.</param>
/// <param name="EnabledOrdinal">The enabling slot, or -1 for always enabled.</param>
/// <param name="RevisionOrdinal">The position revision slot copied into the best output, or -1.</param>
public sealed record ArenaSearchPlan(
    string Name,
    int TokensOrdinal,
    CompiledTopology? Topology,
    int[] ZoneOrdinals,
    int CellCount,
    int TurnOrdinal,
    int VerdictOrdinal,
    long Off,
    int Nodes,
    long JudgeCost,
    int Depth,
    bool Scored,
    SearchShapePlan[] Shapes,
    int[] CodeOrdinals,
    int BestOrdinal = -1,
    int LegalOrdinal = -1,
    int ReachOrdinal = -1,
    int HeldOrdinal = -1,
    int CountsOrdinal = -1,
    long Accept = 1L,
    SearchMethod Method = SearchMethod.Negamax,
    int Iterations = 0,
    ArenaSearchChancePlan? Chance = null,
    int ScoresOrdinal = -1,
    ulong DrawSeed = 0UL,
    int EnabledOrdinal = -1,
    int RevisionOrdinal = -1
) {
    private static bool TryRow(StateCatalog catalog, string job, string? name, out int ordinal, out string reason) {
        ordinal = -1;
        reason = string.Empty;

        if (name is null) {
            return true;
        }

        if (!catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: name
        )) {
            reason = $"search '{job}' names row '{name}', which the catalog does not declare";

            return false;
        }

        ordinal = handle.Ordinal;

        return true;
    }

    /// <summary>Resolves a name-addressed plan against a catalog into an ordinal-addressed one.</summary>
    /// <param name="plan">The name-addressed plan.</param>
    /// <param name="catalog">The catalog whose ordinals address the arena.</param>
    /// <param name="resolved">The ordinal-addressed plan on success; otherwise <see langword="null"/>.</param>
    /// <param name="reason">Why the plan was refused, or empty on success.</param>
    /// <param name="drawSeed">The draw stream a <see cref="SearchMethod.Tree"/> job's playouts advance from.</param>
    /// <returns><see langword="true"/> when every named row resolved and the plan carries nothing the arena walk
    /// does not run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> or <paramref name="catalog"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryResolve(SearchPlan plan, StateCatalog catalog, [NotNullWhen(true)] out ArenaSearchPlan? resolved, out string reason, ulong drawSeed = 0UL) {
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: plan);
        resolved = null;

        if (
            (plan.Scores is not null) &&
            plan.Scored
        ) {
            reason = $"search '{plan.Name}' declares both a score and per-seat scores, and a level folds one of them";

            return false;
        }
        if (
            (plan.Scores is not null) &&
            (plan.Method == SearchMethod.Tree)
        ) {
            reason = $"search '{plan.Name}' declares per-seat scores, which a tree job's alternating-sign backpropagation does not fold";

            return false;
        }
        if (
            !TryRow(catalog: catalog, job: plan.Name, name: plan.Enabled, ordinal: out var enabled, reason: out reason) ||
            !TryRow(catalog: catalog, job: plan.Name, name: plan.Revision, ordinal: out var revision, reason: out reason) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Tokens,
            ordinal: out var tokens,
            reason: out reason
        ) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Turn,
            ordinal: out var turn,
            reason: out reason
        ) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Verdict,
            ordinal: out var verdict,
            reason: out reason
        ) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Best,
            ordinal: out var best,
            reason: out reason
        ) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Legal,
            ordinal: out var legal,
            reason: out reason
        ) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Reach,
            ordinal: out var reach,
            reason: out reason
        ) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Held,
            ordinal: out var held,
            reason: out reason
        ) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Counts,
            ordinal: out var counts,
            reason: out reason
        ) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Scores,
            ordinal: out var scores,
            reason: out reason
        ) ||
            !TryRow(
            catalog: catalog,
            job: plan.Name,
            name: plan.Chance?.Row,
            ordinal: out var chance,
            reason: out reason
        )
        ) {
            return false;
        }

        var zones = new int[plan.Zones.Length];

        for (var index = 0; (index < zones.Length); index++) {
            if (!TryRow(
                catalog: catalog,
                job: plan.Name,
                name: plan.Zones[index],
                ordinal: out zones[index],
                reason: out reason
            )) {
                return false;
            }
        }

        if (plan.Topology is null) {
            foreach (var shape in plan.Shapes) {
                if (shape.Kind is (SearchShapeKind.Jump or SearchShapeKind.Pair)) {
                    reason = $"search '{plan.Name}' declares a {shape.Kind} shape, which walks a board's topology, over a job that names no board";

                    return false;
                }
            }
        }

        var codes = new int[plan.Shapes.Length];

        for (var index = 0; (index < codes.Length); index++) {
            if (!TryRow(
                catalog: catalog,
                job: plan.Name,
                name: plan.Shapes[index].Codes,
                ordinal: out codes[index],
                reason: out reason
            )) {
                return false;
            }
        }

        resolved = new ArenaSearchPlan(
            Name: plan.Name,
            TokensOrdinal: tokens,
            Topology: plan.Topology,
            ZoneOrdinals: zones,
            CellCount: plan.CellCount,
            TurnOrdinal: turn,
            VerdictOrdinal: verdict,
            Off: plan.Off,
            Nodes: plan.Nodes,
            JudgeCost: plan.JudgeCost,
            Depth: plan.Depth,
            Scored: plan.Scored,
            Shapes: plan.Shapes,
            CodeOrdinals: codes,
            Accept: plan.Accept,
            BestOrdinal: best,
            Chance: ((plan.Chance is { } declared)
                ? new ArenaSearchChancePlan(
                    AtDepth: declared.AtDepth,
                    CellCount: declared.CellCount,
                    Outcomes: declared.Outcomes,
                    RowOrdinal: chance,
                    Weights: declared.Weights
                )
                : null),
            CountsOrdinal: counts,
            DrawSeed: drawSeed,
            HeldOrdinal: held,
            Iterations: plan.Iterations,
            LegalOrdinal: legal,
            Method: plan.Method,
            ReachOrdinal: reach,
            EnabledOrdinal: enabled,
            RevisionOrdinal: revision,
            ScoresOrdinal: scores
        );
        reason = string.Empty;

        return true;
    }
}
