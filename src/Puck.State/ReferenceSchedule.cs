using Puck.Maths;

namespace Puck.State;

/// <summary>Portable semantic operation pricing, separate from the legacy heuristic work-unit table.</summary>
public static class ReferenceSchedule {
    /// <summary>Returns an unresolved cycle bound until this operation's portable implementation has calibrated
    /// evidence. A registered heuristic weight is not evidence of a reference-cycle price.</summary>
    /// <param name="operation">The semantic operation, not a host instruction mnemonic.</param>
    /// <param name="kind">The numeric kind of the expression.</param>
    /// <param name="board">The compiled topology and traversal shape, if required.</param>
    public static CostBound OperationCostBound(ExpressionOp operation, CellKind kind = CellKind.Int, BoardQuery? board = null) =>
        CostBound.Unmodeled($"ExpressionOp.{operation} ({kind}) has no calibrated portable cycle price.");
}
