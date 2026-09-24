namespace Puck.State.Rules;

/// <summary>The static pricing every compiled shape reports its cost through. These are heuristic units, not
/// calibrated cycles. A sum no <see cref="long"/> holds is an overflow and an operation nothing prices is
/// unmodeled; both survive every composition, so neither is ever admitted as a number.</summary>
public static partial class RuleWorkBudget {
    /// <summary>Returns the work units a list of effects costs.</summary>
    /// <param name="effects">The compiled effects.</param>
    /// <param name="context">The context the effects were priced against.</param>
    /// <returns>The work units.</returns>
    public static RuleWork EffectsCost(IRuleEffect[] effects, IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: effects);

        var cost = RuleWork.Zero;

        foreach (var effect in effects) {
            cost += effect.Cost(context: context);
        }

        return cost;
    }
    /// <summary>Returns the work units an expression costs: the sum of its operations and live operand reads.</summary>
    /// <param name="tokens">The compiled postfix program.</param>
    /// <param name="context">The context the program was priced against.</param>
    /// <returns>The work units.</returns>
    public static RuleWork ExpressionCost(CompiledExpressionToken[] tokens, IRuleCostContext context) => ExpressionCost(
        context: context,
        kind: CellKind.Int,
        tokens: tokens
    );
    /// <summary>Returns the work units an expression costs in one numeric kind. A fold runs its body once per member
    /// and a call once per call site, so each is priced as its own walk rather than as the one instruction naming
    /// it.</summary>
    /// <param name="tokens">The compiled postfix program.</param>
    /// <param name="kind">The kind the program computes in.</param>
    /// <param name="context">The context the program was priced against.</param>
    /// <returns>The work units.</returns>
    public static RuleWork ExpressionCost(CompiledExpressionToken[] tokens, CellKind kind, IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        return ExpressionCost(
            context: context,
            kind: kind,
            priced: null,
            tokens: tokens
        );
    }

    /// <summary>The most tokens the compiler evaluates to fold one constant subtree. A longer one is left in the
    /// program for the work sheet to price.</summary>
    public const long MaxFoldSteps = 65_536L;

    /// <summary>Counts the tokens one evaluation of a program runs, every call site and fold member expanded,
    /// saturating at <see cref="long.MaxValue"/>. A call contributes its body's own count, which the body carries,
    /// so the count is linear in the program however many times its bodies are shared.</summary>
    /// <param name="tokens">The compiled postfix program.</param>
    /// <returns>The token count.</returns>
    public static long Steps(ReadOnlySpan<CompiledExpressionToken> tokens) {
        var steps = 0L;

        foreach (var token in tokens) {
            var own = (token switch {
                { Call: { } call } => call.Steps,
                { Fold: { } fold } => SaturatingMultiply(
                    left: Math.Max(
                        val1: 1L,
                        val2: fold.Cells
                    ),
                    right: Steps(tokens: fold.Body)
                ),
                _ => 0L,
            });

            steps = SaturatingAdd(
                left: SaturatingAdd(
                    left: steps,
                    right: 1L
                ),
                right: own
            );
        }

        return steps;
    }

    private static long SaturatingMultiply(long left, long right) => (((left != 0L) && (right > (long.MaxValue / left)))
        ? long.MaxValue
        : (left * right)
    );
    // A body is shared by every site that calls it, so it is priced once per kind and the price reused: walking each
    // call site again would take as long as evaluating the chain does.
    private static RuleWork ExpressionCost(CompiledExpressionToken[] tokens, CellKind kind, IRuleCostContext context, Dictionary<(CompiledExpressionToken[] Body, CellKind Kind), RuleWork>? priced) {
        var cost = RuleWork.Zero;

        foreach (var token in tokens) {
            switch (token) {
                case { Operand: { } operand }:
                    cost += operand.Cost(context: context);

                    break;
                case { Fold: { } fold }:
                    cost += (Math.Max(
                        val1: 1L,
                        val2: fold.Cells
                    ) * ExpressionCost(
                        context: context,
                        kind: fold.MemberKind,
                        priced: priced,
                        tokens: fold.Body
                    ));

                    break;
                case { Call: { } call }:
                    priced ??= new Dictionary<(CompiledExpressionToken[] Body, CellKind Kind), RuleWork>();

                    if (!priced.TryGetValue(
                        key: (call.Body, kind),
                        value: out var body
                    )) {
                        body = ExpressionCost(
                            context: context,
                            kind: kind,
                            priced: priced,
                            tokens: call.Body
                        );
                        priced[(call.Body, kind)] = body;
                    }

                    cost += body;

                    break;
                default:
                    cost += OperationCost(
                        board: token.Board,
                        kind: kind,
                        operation: token.Operation
                    );

                    break;
            }
        }

        return cost;
    }

    /// <summary>Returns the work units a gate costs: each operand and expression read, plus one per comparison whose
    /// side is a program.</summary>
    /// <param name="tokens">The compiled postfix gate.</param>
    /// <param name="context">The context the gate was priced against.</param>
    /// <returns>The work units.</returns>
    public static RuleWork GateCost(GateToken[] tokens, IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var cost = RuleWork.Zero;

        foreach (var token in tokens) {
            var sides = (token.LeftSource.Cost(
                context: context,
                kind: token.ValueKind
            ) + token.RightSource.Cost(
                context: context,
                kind: token.ValueKind
            ));

            cost += ((token.LeftSource.IsExpression || token.RightSource.IsExpression)
                ? (1L + sides)
                : sides
            );
        }

        return cost;
    }
    /// <summary>Returns the work units a stable insertion sort of <paramref name="count"/> members over
    /// <paramref name="keys"/> key columns costs, which is the sort every reordering transform runs. Each member is
    /// read, ordered, and written back once; each of the <c>count x (count - 1) / 2</c> pairs a reversed input
    /// compares costs one comparison per key column, the position tiebreak, and one move.</summary>
    /// <param name="count">The most members the sort can hold.</param>
    /// <param name="keys">The key columns one comparison reads.</param>
    /// <returns>The work units.</returns>
    public static RuleWork InsertionSortWork(long count, int keys) {
        var members = Math.Max(
            val1: 0L,
            val2: count
        );
        var perStep = RuleWork.Known(units: (((long)keys) + 2L));
        // One of the two factors is even, so halving it first keeps the pair count exact.
        var pairs = ((((members & 1L) == 0L)
            ? ((members / 2L) * RuleWork.Known(units: Math.Max(
                val1: 0L,
                val2: (members - 1L)
            )))
            : (members * RuleWork.Known(units: ((members - 1L) / 2L)))
        ));

        return ((members * perStep) + ((pairs.IsKnown
            ? (pairs.Units * perStep)
            : pairs
        )));
    }
    /// <summary>Returns the comparisons and moves the runtime's introspective sort can spend ordering
    /// <paramref name="count"/> members, which is the sort behind <see cref="List{T}.Sort()"/> and
    /// <see cref="MemoryExtensions.Sort{T}(Span{T})"/>.</summary>
    /// <remarks>The sort partitions to a depth of <c>2 x (floor(log2 n) + 1)</c> before it falls back to a heap sort,
    /// and insertion-sorts a partition of sixteen or fewer. A member is compared at most twice per partition depth
    /// (the scan and the median-of-three), at most <c>2 x (floor(log2 n) + 1)</c> times by the heap sort, and fewer
    /// than eight times by the insertion sort; each comparison is paired with at most one move. That is
    /// <c>2 x n x (6 x (floor(log2 n) + 1) + 8)</c>.</remarks>
    /// <param name="count">The most members the sort can hold.</param>
    /// <returns>The work units.</returns>
    public static RuleWork IntrosortWork(long count) => ((count < 2L)
        ? RuleWork.Zero
        : ((2L * count) * RuleWork.Known(units: ((6L * SearchSteps(count: count)) + 8L)))
    );
    /// <summary>Returns the probes a binary search or one heap sift over <paramref name="count"/> members makes:
    /// <c>floor(log2 count) + 1</c>, and one for an empty set.</summary>
    /// <param name="count">The members searched.</param>
    /// <returns>The probes.</returns>
    public static long SearchSteps(long count) => ((count < 1L)
        ? 1L
        : (System.Numerics.BitOperations.Log2(value: ((ulong)count)) + 1L)
    );
    /// <summary>Returns the work units one operation token costs. Every price comes from the operator table; a
    /// board token refines its row's ceiling with the topology the query actually walks. An operation the table
    /// does not register is unmodeled, so a program carrying one is admitted under no ceiling.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="kind">The numeric kind the program computes in.</param>
    /// <param name="board">The compiled board query for a board token.</param>
    /// <returns>The work units.</returns>
    public static RuleWork OperationCost(ExpressionOp operation, CellKind kind = CellKind.Int, BoardQuery? board = null) => ((board, operation) switch {
        ( { } shifted, (ExpressionOp.BoardShift or ExpressionOp.BoardImage)) => RuleWork.Known(units: ((shifted.Topology.CellCount / 2) + shifted.Visits)),
        ( { } ray, ExpressionOp.BoardRay) => (((long)ray.Topology.CellCount) * RuleWork.Known(units: ((ray.Topology.CellCount / 2) + ray.Visits))),
        _ => ((ExpressionOperators.Find(operation: operation) is { } row)
            ? RuleWork.Known(units: row.Cost)
            : RuleWork.Unmodeled(reason: $"expression operation {((byte)operation)} is not registered")
        ),
    });
}
