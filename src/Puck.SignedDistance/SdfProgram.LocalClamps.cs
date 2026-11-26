namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    private readonly float[]? m_shapeStepScales;

    /// <summary>Gets how many shape candidates the program clamps before a hard composition.</summary>
    public int ShapeStepClampCount => (m_shapeStepScales?.Count(predicate: static scale => (scale > 0f)) ?? 0);

    /// <summary>Gets the positive Lipschitz multiplier applied to a shape before a hard composition, or one.</summary>
    /// <param name="instructionIndex">The shape's instruction index.</param>
    public float ShapeStepScaleAt(int instructionIndex) =>
        ((m_shapeStepScales?[instructionIndex] is float scale && scale > 0f) ? scale : 1f);

    // A hard blend reads only signs for the zero set. Walk backwards to mark any candidate whose magnitude can
    // reach a later soft blend or field edit, including one that consumes the candidate's enclosing scope.
    private static bool[] FindMagnitudeReaders(Span<SdfInstruction> instructions) {
        var readers = new bool[instructions.Length];
        Span<bool> readsMagnitude = stackalloc bool[SdfProgramBuilder.MaxFieldScopeDepth + 1];
        var depth = 0;
        readsMagnitude[0] = false;

        for (var index = (instructions.Length - 1); index >= 0; index--) {
            var instruction = instructions[index];
            var action = SdfFieldOps.CompositionLipschitzOf(value: instruction.Op);

            if (action == SdfCompositionLipschitzAction.PushField) {
                depth--;
                continue;
            }
            if (action is SdfCompositionLipschitzAction.ShapeBlend or SdfCompositionLipschitzAction.PopField) {
                readsMagnitude[depth] |= !SdfFieldBlends.HardOf(value: ((SdfBlendOp)instruction.Blend));
                if (action == SdfCompositionLipschitzAction.PopField) {
                    readsMagnitude[depth + 1] = readsMagnitude[depth];
                    depth++;
                } else {
                    readers[index] = readsMagnitude[depth];
                }
            } else if ((SdfOpRoles.Of(op: instruction.Op) == SdfOpRole.Field) &&
                (SdfFieldOps.StreamOf(value: instruction.Op) != SdfReliefStreamRole.ReliefRow)) {
                readsMagnitude[depth] = true;
            }
        }

        return readers;
    }
}
