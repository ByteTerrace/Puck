namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    internal bool EvaluateFlockAffinity(CompiledExpressionToken[] program, int observer, int neighbor, out long value) {
        var left = m_boundLeft;
        var right = m_boundRight;

        m_boundLeft = observer;
        m_boundRight = neighbor;
        try {
            // A compiled affinity operand reads only state-backed facts, which cannot change during population
            // movement, so a body observation stays in the population's frozen spatial image.
            return RuleExpressions.TryEvaluate(
                fault: out _,
                kind: CellKind.Fixed,
                program: program,
                reader: this,
                value: out value
            );
        } finally {
            m_boundLeft = left;
            m_boundRight = right;
        }
    }
}
