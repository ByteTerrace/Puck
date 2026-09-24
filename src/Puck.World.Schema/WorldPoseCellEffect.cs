using Puck.Maths;
using Puck.State.Rules;

namespace Puck.World;

/// <summary>Places a body at a cell resolved against the current anchored topology.</summary>
/// <param name="body">The addressed body.</param>
/// <param name="topology">The topology name.</param>
/// <param name="expression">The live cell ordinal.</param>
/// <param name="offset">The displacement from its centre.</param>
public sealed class WorldPoseCellEffect(WorldBodyRef body, string topology, CompiledExpressionToken[] expression, FixedVector3 offset) : WorldFactEffect(describe: $"poseCell {topology}") {
    /// <summary>Gets the addressed body.</summary>
    public WorldBodyRef Body { get; } = body;
    /// <summary>Gets the topology name.</summary>
    public string Topology { get; } = topology;
    /// <summary>Gets the cell expression.</summary>
    public CompiledExpressionToken[] Expression { get; } = expression;
    /// <summary>Gets the displacement from the cell centre.</summary>
    public FixedVector3 Offset { get; } = offset;

    /// <inheritdoc/>
    public override bool SubmitsMutation => false;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        RuleDataflow.CollectExpression(into: into, tokens: Expression);
        if (Body.RowOrdinal >= 0) {
            into.Add(item: new CellAccess(IsSet: false, Key: Body.Key, RowOrdinal: Body.RowOrdinal));
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (1L + RuleWorkBudget.ExpressionCost(Expression, context));
}
