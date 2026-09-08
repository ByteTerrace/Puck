namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool EvaluateFlockAffinity(CompiledExpressionToken[] program, int observer, int neighbor, out long value) {
        var left = m_evaluator.BoundLeft;
        var right = m_evaluator.BoundRight;
        m_evaluator.BoundLeft = observer;
        m_evaluator.BoundRight = neighbor;
        try {
            // Compiled affinity operands read only state-backed facts, which cannot change during
            // population movement. Body observations stay in the population's frozen spatial image.
            return TryEvaluateExpression(program, CellKind.Fixed, m_lastCompletedTick, out value);
        } finally { m_evaluator.BoundLeft = left; m_evaluator.BoundRight = right; }
    }
}
