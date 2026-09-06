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

/// <summary>How a job with a score compares plies.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldSearchMethod>))]
public enum WorldSearchMethod : byte {
    /// <summary>Iterative-deepening negamax with alpha-beta and a transposition table, the score read at the depth cap.</summary>
    Negamax,
    /// <summary>UCB1 tree search over a bounded node pool with seeded playouts, the score read where no candidate is
    /// accepted or at the depth cap, the most-visited root move landed.</summary>
    Tree,
}

/// <summary>Which board-state change one candidate shape makes. <see cref="Relocate"/> is the section's original,
/// still-default shape.</summary>
public enum WorldSearchShapeKind : byte {
    /// <summary>One own token moves onto any other cell.</summary>
    Relocate,
    /// <summary>A token off the board enters an empty cell.</summary>
    Drop,
    /// <summary>The walked token steps two cells along a direction, over a token that leaves the board.</summary>
    Jump,
    /// <summary>One token relocates and its code changes to one of an authored list (<see cref="WorldSearchShape.Promote"/>).</summary>
    Promote,
    /// <summary>The walked token and a second, fixed token relocate together.</summary>
    Pair,
    /// <summary>The walked token, at one end of its ordered zone, moves onto another of the job's zones.</summary>
    Transfer,
}

/// <summary>One authored candidate shape a search job enumerates, ahead of token and target/direction in the walk's
/// fixed order. Every shape but <see cref="Drop"/> requires the walked token on the board; <see cref="Drop"/>
/// requires it off. Absent from <see cref="WorldSearchRow.Shapes"/>, a job's default and only shape is
/// <see cref="Relocate"/> with <c>displace: true</c> — the one candidate this section always ran.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorldSearchShape.Relocate), "relocate")]
[JsonDerivedType(typeof(WorldSearchShape.Drop), "drop")]
[JsonDerivedType(typeof(WorldSearchShape.Jump), "jump")]
[JsonDerivedType(typeof(WorldSearchShape.Paired), "pair")]
[JsonDerivedType(typeof(WorldSearchShape.Promote), "promote")]
[JsonDerivedType(typeof(WorldSearchShape.Transferred), "transfer")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record WorldSearchShape {
    /// <summary>One own token moves onto any other cell of the board. <see cref="Displace"/> (default true) evicts
    /// whatever token already stood on the target — the section's original, unconditional behavior; false leaves it
    /// standing, so a judge rule that reads two tokens sharing a cell decides the candidate itself.</summary>
    /// <param name="Displace">Whether the token standing on the target leaves the board.</param>
    public sealed record Relocate(bool Displace = true) : WorldSearchShape;
    /// <summary>A token whose current value is off the board (see <see cref="WorldSearchRow.Tokens"/>'s remarks)
    /// enters any cell no other token stands on.</summary>
    public sealed record Drop : WorldSearchShape;
    /// <summary>The walked token steps two cells along one topology direction, over a token standing on the
    /// intermediate cell — which leaves the board — onto an empty destination; a direction with no such occupied
    /// intermediate, or whose destination is not empty, is not a candidate.</summary>
    /// <param name="Over">The directions tried, in the topology's own vocabulary (<c>CompiledTopology.Direction</c>).
    /// The single-element list <c>["any"]</c> tries every direction the topology declares.</param>
    public sealed record Jump(IReadOnlyList<string> Over) : WorldSearchShape;
    /// <summary>The walked token relocates to the target cell and a second, fixed token — named by <see cref="With"/>,
    /// a cell key of <see cref="WorldSearchRow.Tokens"/> — relocates by the same lattice translation
    /// (<c>CompiledTopology.TryTranslation</c>: the axial step on a grid, ring, hex, or box), provided its own
    /// destination is empty. A graph or tiling has no translations and refuses the shape. The minimal two-token
    /// primitive a castle's rook needs, not a general rule for every pair's own reach (see the schema README's
    /// search section for the reasoning).</summary>
    /// <param name="With">The companion token's cell key in <see cref="WorldSearchRow.Tokens"/>.</param>
    public sealed record Paired(string With) : WorldSearchShape;
    /// <summary>The walked token relocates onto any other cell, evicting what stood there, and its own code in
    /// <see cref="Codes"/> becomes one of <see cref="To"/> — one candidate per (cell, code). The judge decides where
    /// a code may change; the shape only offers the change.</summary>
    /// <param name="Codes">An integer row keyed by the tokens holding each token's code.</param>
    /// <param name="To">The codes a token may take, at most <see cref="WorldSearchCapacity.MaxPromotions"/>.</param>
    public sealed record Promote(string Codes, IReadOnlyList<long> To) : WorldSearchShape;
    /// <summary>The walked token — standing at the <see cref="Selector"/> end of one of the job's
    /// <see cref="WorldSearchRow.Zones"/> — moves onto any other of them, landing last (the top of the pile) or, with
    /// <see cref="InsertFirst"/>, first: the <c>transfer</c> transform as a candidate, so pile order is what the
    /// zones already hold. A token anywhere but the selected end, or a full destination, is not a candidate. The only
    /// shape a zone job enumerates, and refused by a board job.</summary>
    /// <param name="Selector">Which end of its zone a token must stand at to move: <see cref="ZoneSelector.Last"/>
    /// (default, the top of the pile) or <see cref="ZoneSelector.First"/>.</param>
    /// <param name="InsertFirst">Whether the token lands first in the destination rather than last.</param>
    public sealed record Transferred(ZoneSelector Selector = ZoneSelector.Last, bool InsertFirst = false) : WorldSearchShape;
}

/// <summary>One job's compiled shape: its kind, and the data the runtime resolves a candidate from.
/// <see cref="Directions"/> is populated for <see cref="WorldSearchShapeKind.Jump"/> alone — the resolved direction
/// ordinals <see cref="WorldSearchShape.Jump.Over"/> names. <see cref="PairWithIndex"/> is populated for
/// <see cref="WorldSearchShapeKind.Pair"/> alone — the fixed companion's ordinal in the job's tokens row.</summary>
/// <param name="Kind">The shape.</param>
/// <param name="Displace">Whether a <see cref="WorldSearchShapeKind.Relocate"/> evicts the token standing on the
/// target; unused by every other kind.</param>
/// <param name="Directions">The resolved direction ordinals a <see cref="WorldSearchShapeKind.Jump"/> tries.</param>
/// <param name="PairWithIndex">The companion token's ordinal for a <see cref="WorldSearchShapeKind.Pair"/>, or -1.</param>
/// <param name="Codes">The codes row a <see cref="WorldSearchShapeKind.Promote"/> writes, or <see langword="null"/>.</param>
/// <param name="PromoteTo">The codes a <see cref="WorldSearchShapeKind.Promote"/> offers, or <see langword="null"/>.</param>
/// <param name="Selector">The zone end a <see cref="WorldSearchShapeKind.Transfer"/> moves from.</param>
/// <param name="InsertFirst">Whether a <see cref="WorldSearchShapeKind.Transfer"/> lands first rather than last.</param>
public sealed record WorldSearchShapePlan(WorldSearchShapeKind Kind, bool Displace, int[] Directions, int PairWithIndex, string? Codes = null, long[]? PromoteTo = null, ZoneSelector Selector = ZoneSelector.Last, bool InsertFirst = false) {
    /// <summary>Gets how many candidates this shape enumerates per token: every cell (a board's cells, or a zone
    /// job's zones) for every kind but <see cref="WorldSearchShapeKind.Jump"/>, which enumerates its resolved
    /// directions instead, and <see cref="WorldSearchShapeKind.Promote"/>, which offers every code on every cell.</summary>
    /// <param name="cellCount">The job's cell count.</param>
    public int CandidateCount(int cellCount) => Kind switch {
        WorldSearchShapeKind.Jump => Directions.Length,
        WorldSearchShapeKind.Promote => (cellCount * (PromoteTo?.Length ?? 0)),
        _ => cellCount,
    };
}

/// <summary>One search job.</summary>
/// <param name="Name">The stable job name.</param>
/// <param name="Tokens">The keyed integer row whose cells are the tokens and whose values are the board cells they
/// stand on; a value that is no cell is a token off the board, which the job leaves alone.</param>
/// <param name="Board">The board row over the topology the token values index; absent for a job over
/// <paramref name="Zones"/>.</param>
/// <param name="Zones">For a job over piles: the ordered zones (<c>keysOf</c> rows with <c>ordered</c>, all over the
/// token domain <paramref name="Tokens"/> names) whose ordinals are the job's cells — a token's cell is the zone it
/// stands in, <c>legal</c> masks and <c>best.to</c> name zones, and the one shape is <c>transfer</c>. Exactly one of
/// <paramref name="Board"/> and <paramref name="Zones"/> is authored.</param>
/// <param name="Turn">The slot row whose change marks an accepted relocation; absent, the tabletop board binding
/// anchoring <paramref name="Board"/> supplies it.</param>
/// <param name="Verdict">The slot row the rules judge a relocation into; absent, the same board binding supplies it.</param>
/// <param name="Shapes">The candidate shapes the walk enumerates, in declared order, ahead of token and
/// target/direction; absent or empty, the one default shape (<see cref="WorldSearchShape.Relocate"/>,
/// <c>displace: true</c>).</param>
/// <param name="Legal">An integer row keyed by the tokens receiving, per token, the mask of cells it may relocate to;
/// requires a board of at most 64 cells.</param>
/// <param name="Reach">An integer board row over the same topology as <paramref name="Board"/> receiving, at 1, the
/// cells <paramref name="Held"/>'s named token may reach and, at its own empty value, every other cell; unlike
/// <paramref name="Legal"/>, works for a board of any size. Authored together with <paramref name="Held"/>.</param>
/// <param name="Held">An integer slot row naming the token whose accepted destinations are painted into
/// <paramref name="Reach"/> — its own ordinal in <paramref name="Tokens"/>'s cell order. A value out of range paints
/// nothing.</param>
/// <param name="Counts">An integer row keyed by the tokens receiving, per token, how many candidates it accepted;
/// unlike <paramref name="Legal"/>, works for a board of any size.</param>
/// <param name="Nodes">The relocations judged per tick, at most what the work sheet leaves; absent derives that.</param>
/// <param name="Depth">How many plies the job searches ahead; the depth-one walk this section always ran. A depth
/// past one asks what the position is worth after the ply, not merely whether it is legal, and requires
/// <paramref name="Score"/>.</param>
/// <param name="Score">An infix expression, in the rule expression grammar, evaluated over the frame after a ply from
/// the perspective of the side that made it; iterative-deepening negamax with alpha-beta compares it across plies.
/// Required when <paramref name="Depth"/> exceeds one, or <paramref name="Best"/> is authored.</param>
/// <param name="Best">A keyed integer row receiving the deepest completed depth's answer: <c>token</c> (the mover's
/// ordinal in <paramref name="Tokens"/>), <c>to</c> (its destination cell), and <c>score</c> (the negamax value).</param>
/// <param name="Method">How plies are compared by the score: <see cref="WorldSearchMethod.Negamax"/> to the depth cap,
/// or <see cref="WorldSearchMethod.Tree"/>, which reads the score where no candidate is accepted or at the cap.</param>
/// <param name="Iterations">How many tree iterations a <see cref="WorldSearchMethod.Tree"/> job runs before it lands.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSearchRow(
    string Name,
    string Tokens,
    string? Board = null,
    IReadOnlyList<string>? Zones = null,
    string? Turn = null,
    string? Verdict = null,
    IReadOnlyList<WorldSearchShape>? Shapes = null,
    string? Legal = null,
    string? Reach = null,
    string? Held = null,
    string? Counts = null,
    int? Nodes = null,
    int Depth = 1,
    string? Score = null,
    string? Best = null,
    WorldSearchMethod Method = WorldSearchMethod.Negamax,
    int Iterations = 256
) {
    /// <summary>The one candidate shape a job with none authored enumerates: a plain relocation that evicts
    /// whatever stood on the target — this section's original, unconditional behavior.</summary>
    private static readonly IReadOnlyList<WorldSearchShape> s_defaultShapes = [new WorldSearchShape.Relocate(Displace: true)];

    /// <summary>Gets the shapes the walk enumerates: <see cref="Shapes"/> when authored and non-empty, otherwise
    /// the one default relocate shape.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldSearchShape> EffectiveShapes => ((Shapes is { Count: > 0 }) ? Shapes : s_defaultShapes);
}

/// <summary>Hard bounds for the search section.</summary>
public static class WorldSearchCapacity {
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
}

/// <summary>One job's derived plan: every row resolved, the off-board value, the compiled shapes, and the per-tick
/// node quota.</summary>
/// <param name="Row">The authored job.</param>
/// <param name="Topology">The board's topology, or <see langword="null"/> for a job over zones.</param>
/// <param name="Zones">The zone rows a zone job's cells are, in authored order; empty for a board job.</param>
/// <param name="CellCount">How many cells the job has: the board's, or the zone count.</param>
/// <param name="Turn">The turn row.</param>
/// <param name="Verdict">The verdict row.</param>
/// <param name="Off">The token value meaning off the board.</param>
/// <param name="Nodes">The relocations judged per tick.</param>
/// <param name="JudgeCost">The work units one judge run costs.</param>
/// <param name="Depth">How many plies the job searches ahead.</param>
/// <param name="Score">The compiled score program, or <see langword="null"/> when the job carries none.</param>
/// <param name="Best">The best-move output row, or <see langword="null"/>.</param>
/// <param name="Shapes">The compiled candidate shapes, in declared order.</param>
/// <param name="Accept">The verdict value that accepts a candidate — the board binding's, or 1 without one.</param>
/// <param name="Method">How the job compares plies by its score.</param>
/// <param name="Iterations">The tree iterations a <see cref="WorldSearchMethod.Tree"/> job runs.</param>
public sealed record WorldSearchPlan(WorldSearchRow Row, CompiledTopology? Topology, string[] Zones, int CellCount, string Turn, string Verdict, long Off, int Nodes, long JudgeCost, int Depth, CompiledExpressionToken[]? Score, string? Best, WorldSearchShapePlan[] Shapes, long Accept = 1L, WorldSearchMethod Method = WorldSearchMethod.Negamax, int Iterations = 0);

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

    /// <summary>Compiles a job's authored shapes against its board topology and tokens row.</summary>
    /// <param name="definition">The world the shapes' rows resolve against.</param>
    /// <param name="row">The job.</param>
    /// <param name="topology">The board's topology.</param>
    /// <param name="tokens">The tokens row.</param>
    /// <param name="shapes">The compiled shapes, in declared order.</param>
    /// <param name="reason">Why a shape does not compile, or empty.</param>
    private static bool TryCompileShapes(WorldDefinition definition, WorldSearchRow row, CompiledTopology? topology, WorldStateRow tokens, out WorldSearchShapePlan[] shapes, out string reason) {
        var authored = row.EffectiveShapes;

        shapes = [];

        if ((authored.Count == 0) || (authored.Count > WorldSearchCapacity.MaxShapesPerJob)) {
            reason = $"search '{row.Name}' declares {authored.Count} shapes; the count must lie in 1..{WorldSearchCapacity.MaxShapesPerJob}";

            return false;
        }

        var compiled = new WorldSearchShapePlan[authored.Count];

        for (var index = 0; index < authored.Count; index++) {
            if ((topology is null) != (authored[index] is WorldSearchShape.Transferred)) {
                reason = ((topology is null)
                    ? $"search '{row.Name}' shape[{index}] moves on a board, and a job over zones has none; its one shape is transfer"
                    : $"search '{row.Name}' shape[{index}] transfer moves between zones, and a job over a board has none");

                return false;
            }

            switch (authored[index]) {
                case WorldSearchShape.Transferred transfer:
                    if (transfer.Selector is not (ZoneSelector.First or ZoneSelector.Last)) {
                        reason = $"search '{row.Name}' shape[{index}] transfer moves a token from the first or last end of its zone";

                        return false;
                    }

                    compiled[index] = new WorldSearchShapePlan(Kind: WorldSearchShapeKind.Transfer, Displace: false, Directions: [], PairWithIndex: -1, Selector: transfer.Selector, InsertFirst: transfer.InsertFirst);

                    break;
                case WorldSearchShape.Relocate relocate:
                    compiled[index] = new WorldSearchShapePlan(Kind: WorldSearchShapeKind.Relocate, Displace: relocate.Displace, Directions: [], PairWithIndex: -1);

                    break;
                case WorldSearchShape.Drop:
                    compiled[index] = new WorldSearchShapePlan(Kind: WorldSearchShapeKind.Drop, Displace: false, Directions: [], PairWithIndex: -1);

                    break;
                case WorldSearchShape.Jump jump: {
                    if (jump.Over is not { Count: > 0 }) {
                        reason = $"search '{row.Name}' shape[{index}] jump names no 'over' direction";

                        return false;
                    }

                    int[] directions;

                    if ((jump.Over.Count == 1) && string.Equals(a: jump.Over[0], b: "any", comparisonType: StringComparison.Ordinal)) {
                        directions = new int[topology!.DirectionCount];

                        for (var direction = 0; direction < directions.Length; direction++) {
                            directions[direction] = direction;
                        }
                    } else {
                        directions = new int[jump.Over.Count];

                        for (var index2 = 0; index2 < jump.Over.Count; index2++) {
                            var resolved = topology!.Direction(token: jump.Over[index2]);

                            if (resolved < 0) {
                                reason = $"search '{row.Name}' shape[{index}] jump names direction '{jump.Over[index2]}' the board's topology does not declare";

                                return false;
                            }

                            directions[index2] = resolved;
                        }
                    }

                    compiled[index] = new WorldSearchShapePlan(Kind: WorldSearchShapeKind.Jump, Displace: false, Directions: directions, PairWithIndex: -1);

                    break;
                }
                case WorldSearchShape.Paired pair: {
                    if (!topology!.HasTranslations) {
                        reason = $"search '{row.Name}' shape[{index}] pair carries one translation to two tokens, and a {topology.Kind} board has no translations";

                        return false;
                    }

                    var companion = -1;

                    if (tokens.Cells is { } tokenCells) {
                        for (var t = 0; t < tokenCells.Count; t++) {
                            if (string.Equals(a: tokenCells[t].Key.Value, b: pair.With, comparisonType: StringComparison.Ordinal)) {
                                companion = t;

                                break;
                            }
                        }
                    }
                    if (companion < 0) {
                        reason = $"search '{row.Name}' shape[{index}] pair 'with' names no cell '{pair.With}' of '{row.Tokens}'";

                        return false;
                    }

                    compiled[index] = new WorldSearchShapePlan(Kind: WorldSearchShapeKind.Pair, Displace: false, Directions: [], PairWithIndex: companion);

                    break;
                }
                case WorldSearchShape.Promote promote: {
                    if (promote.To is not { Count: > 0 and <= WorldSearchCapacity.MaxPromotions }) {
                        reason = $"search '{row.Name}' shape[{index}] promote offers {promote.To?.Count ?? 0} codes; the count must lie in 1..{WorldSearchCapacity.MaxPromotions}";

                        return false;
                    }

                    var codesRow = WorldDefinitionRows.FindStateRow(rows: definition.State, name: promote.Codes);

                    if ((codesRow is not { IsKeyed: true, Kind: CellKind.Int }) || ((codesRow.Cells?.Count ?? 0) != (tokens.Cells?.Count ?? 0))) {
                        reason = $"search '{row.Name}' shape[{index}] promote 'codes' must be an integer row keyed like '{row.Tokens}'";

                        return false;
                    }

                    compiled[index] = new WorldSearchShapePlan(Kind: WorldSearchShapeKind.Promote, Displace: true, Directions: [], PairWithIndex: -1, Codes: promote.Codes, PromoteTo: [.. promote.To]);

                    break;
                }
                default:
                    reason = $"search '{row.Name}' shape[{index}] is an unrecognized shape";

                    return false;
            }
        }

        shapes = compiled;
        reason = string.Empty;

        return true;
    }

    /// <summary>Derives one job's plan, or names why the job cannot run.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="row">The job.</param>
    /// <param name="judgeCost">The work units one judge run costs.</param>
    /// <param name="leftover">The work units the sheet leaves per tick, shared by every job.</param>
    /// <param name="context">The rule compile context, for compiling <see cref="WorldSearchRow.Score"/>.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="reason">Why the job cannot run, or empty.</param>
    public static bool TryPlan(WorldDefinition definition, WorldSearchRow row, long judgeCost, long leftover, WorldRuleCompileContext context, out WorldSearchPlan? plan, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: row);
        ArgumentNullException.ThrowIfNull(argument: context);
        plan = null;

        var tokens = WorldDefinitionRows.FindStateRow(rows: definition.State, name: row.Tokens);
        CompiledTopology? topology = null;
        StateDomain.CellsOf? cells = null;
        string[] zones = [];

        if ((row.Board is null) == (row.Zones is null)) {
            reason = $"search '{row.Name}' names exactly one of board and zones";

            return false;
        }
        if (row.Zones is { } zoneNames) {
            // A zone job's tokens row is the zones' shared domain: its keys are the tokens, its values are its own.
            if (tokens is not { IsKeyed: true } || (tokens.Kind == CellKind.Text) || (tokens.EffectiveDomain is StateDomain.CellsOf or StateDomain.Ring or StateDomain.KeysOf { Ordered: true })) {
                reason = $"search '{row.Name}' tokens '{row.Tokens}' must be the keyed row the zones draw their tokens from";

                return false;
            }
            if (zoneNames.Count < 2) {
                reason = $"search '{row.Name}' zones names {zoneNames.Count}; a transfer needs at least two";

                return false;
            }

            zones = new string[zoneNames.Count];

            for (var index = 0; index < zoneNames.Count; index++) {
                var zoneName = zoneNames[index];

                if ((WorldDefinitionRows.FindStateRow(rows: definition.State, name: zoneName) is not { } zone) || (zone.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } keysOf) || !string.Equals(a: keysOf.Row.Value, b: row.Tokens, comparisonType: StringComparison.Ordinal)) {
                    reason = $"search '{row.Name}' zones[{index}] '{zoneName}' must be an ordered zone over '{row.Tokens}'";

                    return false;
                }
                if (Array.IndexOf(array: zones, value: zoneName, startIndex: 0, count: index) >= 0) {
                    reason = $"search '{row.Name}' zones names '{zoneName}' more than once";

                    return false;
                }

                zones[index] = zoneName;
            }
        } else {
            var board = WorldDefinitionRows.FindStateRow(rows: definition.State, name: row.Board!);

            if (tokens is not { IsKeyed: true, Kind: CellKind.Int } || (tokens.EffectiveDomain is StateDomain.CellsOf or StateDomain.Ring)) {
                reason = $"search '{row.Name}' tokens '{row.Tokens}' must be a keyed integer row";

                return false;
            }
            if ((board?.EffectiveDomain is not StateDomain.CellsOf boardCells) || (board.Kind != CellKind.Int) || (WorldTopologyCompilation.Find(definition, boardCells.Topology) is not { } boardTopology) || (boardTopology.Kind == TopologyKind.Field)) {
                reason = $"search '{row.Name}' board '{row.Board}' must be an integer board over a discrete topology";

                return false;
            }

            cells = boardCells;
            topology = boardTopology;
        }

        var cellCount = (topology?.CellCount ?? zones.Length);

        if (!TryCompileShapes(definition: definition, row: row, topology: topology, tokens: tokens, shapes: out var shapes, reason: out reason)) {
            return false;
        }

        WorldPlacementBoard? binding = null;

        foreach (var placement in definition.Placements) {
            if ((row.Board is not null) && (placement.Board is { } facet) && string.Equals(a: facet.Occupancy, b: row.Board, comparisonType: StringComparison.Ordinal)) {
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
            if (cellCount > BoardMask.MaxCells) {
                reason = $"search '{row.Name}' legal masks need at most {BoardMask.MaxCells} cells; the job has {cellCount}";

                return false;
            }
        }
        if ((row.Reach is not null) || (row.Held is not null)) {
            if ((row.Reach is null) || (row.Held is null)) {
                reason = $"search '{row.Name}' reach and held must be authored together";

                return false;
            }
            if (cells is null) {
                reason = $"search '{row.Name}' reach paints a board, and a job over zones has none; read legal or counts instead";

                return false;
            }
            if (WorldDefinitionRows.FindStateRow(rows: definition.State, name: row.Reach) is not { Kind: CellKind.Int, EffectiveDomain: StateDomain.CellsOf reachDomain } || !string.Equals(a: reachDomain.Topology, b: cells.Topology, comparisonType: StringComparison.Ordinal)) {
                reason = $"search '{row.Name}' reach '{row.Reach}' must be an integer board over the same topology as '{row.Board}'";

                return false;
            }
            if (reachDomain.Empty == 1L) {
                reason = $"search '{row.Name}' reach '{row.Reach}' declares empty 1, the value the job paints a reachable cell with";

                return false;
            }
            if (WorldDefinitionRows.FindStateRow(rows: definition.State, name: row.Held) is not { IsSlot: true, Kind: CellKind.Int }) {
                reason = $"search '{row.Name}' held '{row.Held}' must be an integer slot row";

                return false;
            }
        }
        if (row.Counts is { } countsName) {
            if (WorldDefinitionRows.FindStateRow(rows: definition.State, name: countsName) is not { Kind: CellKind.Int, EffectiveDomain: StateDomain.KeysOf countsKeysOf } || !string.Equals(a: countsKeysOf.Row.Value, b: row.Tokens, comparisonType: StringComparison.Ordinal)) {
                reason = $"search '{row.Name}' counts '{countsName}' must be an integer row keyed by '{row.Tokens}'";

                return false;
            }
        }

        var off = ((topology is null) ? -1L : (tokens.Min ?? -1L));

        if ((off >= 0L) && (off < cellCount)) {
            reason = $"search '{row.Name}' tokens '{row.Tokens}' declares min {off}, a cell of the board; a token off the board needs a value that is no cell";

            return false;
        }
        if ((row.Depth < 1) || (row.Depth > WorldSearchCapacity.MaxDepth)) {
            reason = $"search '{row.Name}' depth {row.Depth} must lie in 1..{WorldSearchCapacity.MaxDepth}";

            return false;
        }
        if (((row.Depth > 1) || (row.Best is not null) || (row.Method == WorldSearchMethod.Tree)) && (row.Score is null)) {
            reason = $"search '{row.Name}' names no score — a depth past one, a best row, or the tree method needs one to compare plies by";

            return false;
        }
        if (!Enum.IsDefined(value: row.Method)) {
            reason = $"search '{row.Name}' names an unknown method";

            return false;
        }
        if ((row.Iterations < 1) || (row.Iterations > WorldSearchCapacity.MaxIterations)) {
            reason = $"search '{row.Name}' iterations {row.Iterations} must lie in 1..{WorldSearchCapacity.MaxIterations}";

            return false;
        }

        CompiledExpressionToken[]? score = null;

        if (row.Score is { } scoreText) {
            if (!ExpressionSpelling.TryParse(text: scoreText, tokens: out var scoreTokens, error: out var parseError)) {
                reason = $"search '{row.Name}' score '{scoreText}' does not parse: {parseError}";

                return false;
            }

            try {
                score = RuleCompiler.CompileExpression(expression: new ValueExpression(Tokens: scoreTokens), kind: CellKind.Int, ruleName: row.Name, verb: "search score", context: context);
            } catch (RuleException exception) {
                reason = exception.Message;

                return false;
            }
            if (RuleDataflow.ExpressionReadsHost(tokens: score)) {
                reason = $"search '{row.Name}' score reads a fact only the world host answers; a frame cannot evaluate it";

                return false;
            }
        }
        if (row.Best is { } bestName) {
            if (WorldDefinitionRows.FindStateRow(rows: definition.State, name: bestName) is not { IsKeyed: true, Kind: CellKind.Int } bestRow) {
                reason = $"search '{row.Name}' best '{bestName}' must be a keyed integer row";

                return false;
            }

            foreach (var cell in new[] { "token", "to", "score" }) {
                if (!bestRow.HasCell(key: cell)) {
                    reason = $"search '{row.Name}' best '{bestName}' must declare cells 'token', 'to', and 'score'";

                    return false;
                }
            }
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

        plan = new WorldSearchPlan(Row: row, Topology: topology, Zones: zones, CellCount: cellCount, Turn: turnName, Verdict: verdictName, Off: off, Nodes: (row.Nodes ?? derived), JudgeCost: judgeCost, Depth: row.Depth, Score: score, Best: row.Best, Shapes: shapes, Accept: (binding?.Accept ?? 1L), Method: row.Method, Iterations: row.Iterations);
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
            if (!TryPlan(definition: definition, row: rows[index], judgeCost: judgeCost, leftover: leftover, context: context, plan: out var plan, reason: out reason)) {
                return false;
            }

            plans[index] = plan!;
        }

        reason = string.Empty;

        return true;
    }
}
