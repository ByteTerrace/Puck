namespace Puck.State.Rules;

/// <summary>The static pricing every compiled shape reports its cost through. These are heuristic units, not
/// calibrated cycles; the arithmetic saturates so a pathological program reports the ceiling rather than wrapping.</summary>
public static partial class RuleWorkBudget {
    /// <summary>Returns the work units a list of effects costs.</summary>
    /// <param name="effects">The compiled effects.</param>
    /// <param name="context">The context the effects were priced against.</param>
    /// <returns>The work units.</returns>
    public static long EffectsCost(IRuleEffect[] effects, IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: effects);

        var cost = 0L;

        foreach (var effect in effects) {
            cost = SaturatingAdd(
                left: cost,
                right: effect.Cost(context: context)
            );
        }

        return cost;
    }
    /// <summary>Returns the work units an expression costs: the sum of its operations and live operand reads.</summary>
    /// <param name="tokens">The compiled postfix program.</param>
    /// <param name="context">The context the program was priced against.</param>
    /// <returns>The work units.</returns>
    public static long ExpressionCost(CompiledExpressionToken[] tokens, IRuleCostContext context) => ExpressionCost(
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
    public static long ExpressionCost(CompiledExpressionToken[] tokens, CellKind kind, IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var cost = 0L;

        foreach (var token in tokens) {
            var tokenCost = token switch {
                { Operand: { } operand } => operand.Cost(context: context),
                { Fold: { } fold } => SaturatingMultiply(
                left: Math.Max(
                    val1: 1L,
                    val2: fold.Members
                ),
                right: ExpressionCost(
                    context: context,
                    kind: fold.MemberKind,
                    tokens: fold.Body
                )
            ),
                { Call: { } call } => ExpressionCost(
                context: context,
                kind: kind,
                tokens: call.Body
            ),
                _ => OperationCost(
                board: token.Board,
                kind: kind,
                operation: token.Operation
            ),
            };

            cost = SaturatingAdd(
                left: cost,
                right: tokenCost
            );
        }

        return cost;
    }
    /// <summary>Returns the work units a gate costs: each operand and expression read, plus one per comparison whose
    /// side is a program.</summary>
    /// <param name="tokens">The compiled postfix gate.</param>
    /// <param name="context">The context the gate was priced against.</param>
    /// <returns>The work units.</returns>
    public static long GateCost(GateToken[] tokens, IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var cost = 0L;

        foreach (var token in tokens) {
            var sides = SaturatingAdd(
                left: token.LeftSource.Cost(
                    context: context,
                    kind: token.ValueKind
                ),
                right: token.RightSource.Cost(
                    context: context,
                    kind: token.ValueKind
                )
            );

            cost = SaturatingAdd(
                left: cost,
                right: ((token.LeftSource.IsExpression || token.RightSource.IsExpression)
                ? SaturatingAdd(
                        left: 1L,
                        right: sides
                    )
                : sides)
            );
        }

        return cost;
    }
    /// <summary>Returns the work units one operation token costs. Every price comes from the operator table; a
    /// board token refines its row's ceiling with the topology the query actually walks. An unknown operation
    /// receives the rejecting sentinel, so a program carrying one prices past every ceiling.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="kind">The numeric kind the program computes in.</param>
    /// <param name="board">The compiled board query for a board token.</param>
    /// <returns>The work units.</returns>
    public static long OperationCost(ExpressionOp operation, CellKind kind = CellKind.Int, BoardQuery? board = null) => ((board, operation) switch {
        ( { } shifted, (ExpressionOp.BoardShift or ExpressionOp.BoardImage)) => ((shifted.Topology.CellCount / 2) + shifted.Visits),
        ( { } fill, ExpressionOp.BoardFill) => (((long)fill.Topology.CellCount) * ((fill.Topology.CellCount / 2) + fill.Visits)),
        _ => (ExpressionOperators.Find(operation: operation)?.Cost ?? long.MaxValue),
    });
    /// <summary>Adds, clamping at <see cref="long.MaxValue"/>.</summary>
    /// <param name="left">The left addend.</param>
    /// <param name="right">The right addend.</param>
    /// <returns>The sum.</returns>
    public static long SaturatingAdd(long left, long right) => ((left > (long.MaxValue - right))
        ? long.MaxValue
        : (left + right)
    );
    /// <summary>Multiplies, clamping at <see cref="long.MaxValue"/>.</summary>
    /// <param name="left">The left factor.</param>
    /// <param name="right">The right factor.</param>
    /// <returns>The product.</returns>
    public static long SaturatingMultiply(long left, long right) => (((left == 0L) || (right == 0L))
        ? 0L
        : ((left > (long.MaxValue / right))
            ? long.MaxValue
            : (left * right)
    ));
}
