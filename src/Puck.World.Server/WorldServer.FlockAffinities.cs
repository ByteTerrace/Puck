namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool EvaluateFlockAffinity(CompiledWorldExpressionToken[] program, int observer, int neighbor, out long value) {
        var left = m_boundLeft;
        var right = m_boundRight;
        m_boundLeft = observer;
        m_boundRight = neighbor;
        try {
            // Compiled affinity operands read only state-backed or social facts, which cannot change during
            // population movement. Body observations stay in the population's frozen spatial image.
            return TryEvaluateExpression(program, CellKind.Fixed, m_lastCompletedTick, out value);
        } finally { m_boundLeft = left; m_boundRight = right; }
    }
}
