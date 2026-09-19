namespace Puck.State;

/// <summary>One recursive ply's checkpointed progress: its own (shape, token, candidate) cursor, its negamax
/// window, and the best candidate found so far. The ply's position is not carried — it is the arena under the
/// scopes <see cref="ArenaSearchJobCheckpoint.Scopes"/> names, reopened from them on restore.</summary>
/// <param name="Shape">The shape cursor.</param>
/// <param name="Token">The token cursor.</param>
/// <param name="Target">The candidate cursor.</param>
/// <param name="Alpha">The window's lower edge.</param>
/// <param name="Beta">The window's upper edge.</param>
/// <param name="Best">The best value folded here.</param>
/// <param name="BestToken">The token of the best candidate, or <c>-1</c>.</param>
/// <param name="BestTarget">The target of the best candidate, or <c>-1</c>.</param>
/// <param name="BaseTurn">The turn value the position carried when the ply opened.</param>
/// <param name="Key">The position's key, for the transposition table's store.</param>
/// <param name="AlphaEntry">The window's lower edge at entry.</param>
/// <param name="Seats">The seat vector of the best line found at this ply; empty for a job with no per-seat
/// scores.</param>
public sealed record ArenaSearchLevelCheckpoint(
    int Shape, int Token, int Target, long Alpha, long Beta, long Best, int BestToken, int BestTarget, long BaseTurn, ulong Key, long AlphaEntry, long[]? Seats = null
);
/// <summary>One open candidate scope, named by the candidate that opened it rather than by the columns it wrote: a
/// restore reopens the scope by resolving, applying, and judging that candidate again.</summary>
/// <param name="Shape">The shape's index in the plan.</param>
/// <param name="Token">The walked token's index.</param>
/// <param name="Target">The candidate index within the shape.</param>
public readonly record struct ArenaSearchScopeCheckpoint(int Shape, int Token, int Target);
/// <summary>A job's tree search in flight: the node pool (parallel arrays, <see cref="Count"/> nodes used), the path
/// from the root, the phase and its cursors, and the draw seed.</summary>
/// <param name="Active">Whether the tree search is running.</param>
/// <param name="Phase">The phase cursor.</param>
/// <param name="Count">How many nodes the pool holds.</param>
/// <param name="Iteration">How many iterations have completed.</param>
/// <param name="Seed">The draw stream's position.</param>
/// <param name="ExpandShape">The expansion's shape cursor.</param>
/// <param name="ExpandToken">The expansion's token cursor.</param>
/// <param name="ExpandTarget">The expansion's candidate cursor.</param>
/// <param name="Scan">How many candidates the current playout ply has tried.</param>
/// <param name="Start">The flat candidate index the current playout ply scans from.</param>
/// <param name="PlayoutPlies">How many plies the playout has taken.</param>
/// <param name="PlayCount">How many of the open scopes belong to the playout rather than the path.</param>
/// <param name="Parent">Each node's parent, or <c>-1</c>.</param>
/// <param name="FirstChild">Each node's first child, or <c>-1</c>.</param>
/// <param name="ChildCount">Each node's child count.</param>
/// <param name="Visits">Each node's visit count.</param>
/// <param name="Total">Each node's folded total.</param>
/// <param name="Shape">Each node's candidate shape.</param>
/// <param name="Token">Each node's candidate token.</param>
/// <param name="Target">Each node's candidate index.</param>
/// <param name="Expanded">Whether each node has been expanded.</param>
/// <param name="Path">The path from the root, as node indices.</param>
/// <param name="PathLength">How many entries of <paramref name="Path"/> are live.</param>
public sealed record ArenaSearchTreeCheckpoint(
    bool Active, int Phase, int Count, int Iteration, ulong Seed, int ExpandShape, int ExpandToken, int ExpandTarget, int Scan, int Start, int PlayoutPlies, int PlayCount,
    int[] Parent, int[] FirstChild, int[] ChildCount, long[] Visits, long[] Total, int[] Shape, int[] Token, int[] Target, long[] Expanded,
    int[] Path, int PathLength
);
/// <summary>One job's checkpointed progress.</summary>
/// <param name="Name">The job name.</param>
/// <param name="Stamp">The input fold the job's progress was computed against.</param>
/// <param name="Running">Whether the job is still walking.</param>
/// <param name="Done">Whether the job has landed.</param>
/// <param name="Shape">The root ply's shape cursor.</param>
/// <param name="Token">The root ply's token cursor.</param>
/// <param name="Target">The root ply's candidate cursor.</param>
/// <param name="Count">How many candidates the root accepted.</param>
/// <param name="Legal">One accepted-cell bitmask per token.</param>
/// <param name="Counts">One accepted-candidate count per token.</param>
/// <param name="Wide">One bit-packed accepted-destination set per token; empty when the job paints no reach.</param>
/// <param name="Nodes">How many candidates the job has judged.</param>
/// <param name="BaseTurn">The turn value the root position carried.</param>
/// <param name="TokenCount">How many tokens the job walks.</param>
/// <param name="PassDepth">The depth the current pass searches to.</param>
/// <param name="Active">Which ply is being expanded.</param>
/// <param name="Best">The best value the root folded.</param>
/// <param name="BestToken">The token of the best candidate, or <c>-1</c>.</param>
/// <param name="BestTarget">The target of the best candidate, or <c>-1</c>.</param>
/// <param name="Alpha">The root window's lower edge.</param>
/// <param name="Beta">The root window's upper edge.</param>
/// <param name="Levels">One entry per ply beyond the root.</param>
/// <param name="Scopes">The candidate scopes the job holds, outermost first.</param>
/// <param name="TtKey">The transposition table's keys.</param>
/// <param name="TtValue">The transposition table's values.</param>
/// <param name="TtMeta">The transposition table's depth and bound flags.</param>
/// <param name="Tree">The tree search in flight, or <see langword="null"/>.</param>
public sealed record ArenaSearchJobCheckpoint(
    string Name, ulong Stamp, bool Running, bool Done, int Shape, int Token, int Target, long Count, long[] Legal, long[] Counts, long[] Wide, long Nodes, long BaseTurn, int TokenCount,
    int PassDepth, int Active, long Best, int BestToken, int BestTarget, long Alpha, long Beta, ArenaSearchLevelCheckpoint[] Levels, ArenaSearchScopeCheckpoint[] Scopes,
    ulong[] TtKey, long[] TtValue, long[] TtMeta, ArenaSearchTreeCheckpoint? Tree = null
);
/// <summary>The search's checkpointed state, in job order.</summary>
/// <param name="Jobs">Every installed job's progress.</param>
public sealed record ArenaSearchCheckpoint(ArenaSearchJobCheckpoint[] Jobs) {
    /// <summary>Gets the checkpoint of a search with no jobs.</summary>
    public static ArenaSearchCheckpoint Empty { get; } = new(Jobs: []);
}
