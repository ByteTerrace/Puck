using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>Hard bounds for a search runtime: how many jobs, shapes, and candidates one document (or one
/// hand-built job set) may declare, and the fixed magnitudes its search math never overflows.</summary>
public static class SearchCapacity {
    /// <summary>The most jobs one document declares.</summary>
    public const int MaxJobs = 8;
    /// <summary>The most candidate shapes one job declares.</summary>
    public const int MaxShapesPerJob = 8;
    /// <summary>The most relocations one job judges per tick, whatever the work sheet leaves.</summary>
    public const int MaxNodesPerTick = 4_096;
    /// <summary>The most plies one job searches ahead.</summary>
    public const int MaxDepth = 32;
    /// <summary>The most codes one <c>promote</c> shape offers.</summary>
    public const int MaxPromotions = 8;
    /// <summary>The transposition table's entries per job with a score: a power of two, indexed by the low bits of a
    /// position's frame hash.</summary>
    public const int TranspositionEntries = 1_024;
    /// <summary>The tree nodes one job with an outcome may grow.</summary>
    public const int TreeNodes = 2_048;
    /// <summary>The most tree iterations one job runs before it lands.</summary>
    public const int MaxIterations = 65_536;
    /// <summary>The magnitude a terminal position (no accepted relocation) scores for the side to move, and the
    /// negamax search window's width — shifted down from <see cref="long.MaxValue"/> so a value repeatedly negated
    /// and compared across the deepest authored search never overflows.</summary>
    public const long MateScore = (long.MaxValue >> 2);
    /// <summary>The most outcomes one <see cref="SearchChancePlan"/> may bake — the cross product of its row's own
    /// generator's domain across every one of the row's cells (two six-sided dice bakes 36).</summary>
    public const int MaxChanceOutcomes = 64;
}

/// <summary>How a job with a score compares plies.</summary>
[JsonConverter(typeof(StrictEnumConverter<SearchMethod>))]
public enum SearchMethod : byte {
    /// <summary>Iterative-deepening negamax with alpha-beta and a transposition table, the score read at the depth cap.</summary>
    Negamax,
    /// <summary>UCB1 tree search over a bounded node pool with seeded playouts, the score read where no candidate is
    /// accepted or at the depth cap, the most-visited root move landed.</summary>
    Tree,
}

/// <summary>Which board-state change one candidate shape makes. <see cref="Relocate"/> is the plain default shape.</summary>
public enum SearchShapeKind : byte {
    /// <summary>One own token moves onto any other cell.</summary>
    Relocate,
    /// <summary>A token off the board enters an empty cell.</summary>
    Drop,
    /// <summary>The walked token steps two cells along a direction, over a token that leaves the board.</summary>
    Jump,
    /// <summary>One token relocates and its code changes to one of an authored list.</summary>
    Promote,
    /// <summary>The walked token and a second, fixed token relocate together.</summary>
    Pair,
    /// <summary>The walked token, at one end of its ordered zone, moves onto another of the job's zones.</summary>
    Transfer,
}

/// <summary>One compiled candidate shape a search job enumerates, ahead of token and target/direction in the walk's
/// fixed order. <see cref="Directions"/> is populated for <see cref="SearchShapeKind.Jump"/> alone — the resolved
/// direction ordinals its authored shape names. <see cref="PairWithIndex"/> is populated for
/// <see cref="SearchShapeKind.Pair"/> alone — the fixed companion's ordinal in the job's tokens row.</summary>
/// <param name="Kind">The shape.</param>
/// <param name="Displace">Whether a <see cref="SearchShapeKind.Relocate"/> evicts the token standing on the
/// target; unused by every other kind.</param>
/// <param name="Directions">The resolved direction ordinals a <see cref="SearchShapeKind.Jump"/> tries.</param>
/// <param name="PairWithIndex">The companion token's ordinal for a <see cref="SearchShapeKind.Pair"/>, or -1.</param>
/// <param name="Codes">The codes row a <see cref="SearchShapeKind.Promote"/> writes, or <see langword="null"/>.</param>
/// <param name="PromoteTo">The codes a <see cref="SearchShapeKind.Promote"/> offers, or <see langword="null"/>.</param>
/// <param name="Selector">The zone end a <see cref="SearchShapeKind.Transfer"/> moves from.</param>
/// <param name="InsertFirst">Whether a <see cref="SearchShapeKind.Transfer"/> lands first rather than last.</param>
public sealed record SearchShapePlan(SearchShapeKind Kind, bool Displace, int[] Directions, int PairWithIndex, string? Codes = null, long[]? PromoteTo = null, ZoneSelector Selector = ZoneSelector.Last, bool InsertFirst = false) {
    /// <summary>Gets how many candidates this shape enumerates per token: every cell (a board's cells, or a zone
    /// job's zones) for every kind but <see cref="SearchShapeKind.Jump"/>, which enumerates its resolved directions
    /// instead, and <see cref="SearchShapeKind.Promote"/>, which offers every code on every cell.</summary>
    /// <param name="cellCount">The job's cell count.</param>
    public int CandidateCount(int cellCount) => Kind switch {
        SearchShapeKind.Jump => Directions.Length,
        SearchShapeKind.Promote => (cellCount * (PromoteTo?.Length ?? 0)),
        _ => cellCount,
    };
}

/// <summary>One search job's baked chance node: the ply whose move choice the job's search averages over instead of
/// choosing, and the outcome table a document project bakes once from the row's own declared generator (its cells'
/// cross product — two dice of <c>uniformRange 1..6</c> bake 36 outcomes) so <see cref="SearchRuntime"/> reads pure
/// data. Negamax computes the exact weighted average over every outcome at <see cref="AtDepth"/>; a
/// <see cref="SearchMethod.Tree"/> job instead samples one outcome per playout at the ply its own playout numbering
/// reaches <see cref="AtDepth"/>, from the job's own stream.</summary>
/// <param name="Row">The row a chosen outcome writes.</param>
/// <param name="AtDepth">Negamax: the absolute ply (0 = root) whose move choice is replaced. Tree: the 1-based
/// playout ply at which the playout draws instead of choosing a candidate.</param>
/// <param name="CellCount">How many cells <paramref name="Row"/> has, in its own cell order.</param>
/// <param name="Outcomes">Every outcome's per-cell values, flattened outcome-major (<c>outcome * CellCount + cell</c>).</param>
/// <param name="Weights">Every outcome's relative weight, one per stride of <paramref name="Outcomes"/>.</param>
public sealed record SearchChancePlan(string Row, int AtDepth, int CellCount, long[] Outcomes, ulong[] Weights);

/// <summary>One search job's fully resolved plan — every row it reads or writes, by name, plus the compiled shapes
/// and the per-tick node quota. A document project derives this from its own authored row (validating it against
/// the document, resolving row and topology references) and hands the plan to <see cref="SearchRuntime"/>, which
/// reads nothing else about where the job came from.</summary>
/// <param name="Name">The stable job name, for its status and error reporting.</param>
/// <param name="Tokens">The keyed integer row whose cells are the tokens and whose values are the board cells they
/// stand on; a value that is no cell is a token off the board, which the job leaves alone.</param>
/// <param name="Topology">The board's topology, or <see langword="null"/> for a job over <paramref name="Zones"/>.</param>
/// <param name="Zones">For a job over piles: the ordered zone row names, in authored order; empty for a board job.</param>
/// <param name="CellCount">How many cells the job has: the board's, or the zone count.</param>
/// <param name="Turn">The slot row whose change marks an accepted relocation.</param>
/// <param name="Verdict">The slot row the rules judge a relocation into.</param>
/// <param name="Off">The token value meaning off the board.</param>
/// <param name="Nodes">The relocations judged per tick.</param>
/// <param name="JudgeCost">The work units one judge run costs.</param>
/// <param name="Depth">How many plies the job searches ahead.</param>
/// <param name="Score">The compiled score program, or <see langword="null"/> when the job carries none.</param>
/// <param name="Best">The best-move output row, or <see langword="null"/>.</param>
/// <param name="Shapes">The compiled candidate shapes, in declared order.</param>
/// <param name="Legal">The row keyed by <paramref name="Tokens"/> receiving each token's accepted-cell bitmask, or
/// <see langword="null"/>; requires a board of at most <c>BoardMask.MaxCells</c> cells.</param>
/// <param name="Reach">The board row over <paramref name="Topology"/> painted with <paramref name="Held"/>'s
/// token's accepted destinations, or <see langword="null"/>.</param>
/// <param name="Held">The slot row naming the token <paramref name="Reach"/> paints for, or <see langword="null"/>.</param>
/// <param name="Counts">The row keyed by <paramref name="Tokens"/> receiving each token's accepted-candidate count,
/// or <see langword="null"/>.</param>
/// <param name="Accept">The verdict value that accepts a candidate.</param>
/// <param name="Method">How the job compares plies by its score.</param>
/// <param name="Iterations">The tree iterations a <see cref="SearchMethod.Tree"/> job runs.</param>
/// <param name="Chance">The job's baked chance node, or <see langword="null"/> for a job with none.</param>
public sealed record SearchPlan(
    string Name,
    string Tokens,
    CompiledTopology? Topology,
    string[] Zones,
    int CellCount,
    string Turn,
    string Verdict,
    long Off,
    int Nodes,
    long JudgeCost,
    int Depth,
    CompiledExpressionToken[]? Score,
    string? Best,
    SearchShapePlan[] Shapes,
    string? Legal = null,
    string? Reach = null,
    string? Held = null,
    string? Counts = null,
    long Accept = 1L,
    SearchMethod Method = SearchMethod.Negamax,
    int Iterations = 0,
    SearchChancePlan? Chance = null
);

/// <summary>One state write a finished search job wants applied, through whatever mutation door the document
/// project owns — <see cref="SearchRuntime"/> knows only that a job wants these writes made, never how a document
/// project encodes them on the wire.</summary>
public abstract record SearchWrite {
    /// <summary>Sets one row's cell to a value.</summary>
    /// <param name="Row">The row name.</param>
    /// <param name="Key">The cell key.</param>
    /// <param name="Value">The value.</param>
    public sealed record Cell(string Row, string Key, long Value) : SearchWrite;
    /// <summary>Clears every cell of a board row — issued before a job repaints it sparsely.</summary>
    /// <param name="Row">The row name.</param>
    public sealed record ClearBoard(string Row) : SearchWrite;
}
