namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // Read the already-baked stream, rather than maintaining a second version of the composition/warp analysis.
    // Validation has established one-deep balanced scopes and a single instruction owner throughout each scope.
    private IReadOnlyList<SdfFieldScopeClamp> ReadFieldScopeClamps(int[] instructionOwners) {
        List<SdfFieldScopeClamp>? clamps = null;
        var push = -1;
        var shapes = 0;

        for (var index = 0; index < m_instructions.Length; index++) {
            var instruction = m_instructions[index];
            switch (instruction.Op) {
                case SdfOp.PushField:
                    push = index;
                    shapes = 0;
                    break;
                case SdfOp.ShapeBlend when push >= 0:
                    shapes++;
                    break;
                case SdfOp.PopField:
                    if (instruction.Data1.Y is > 0f and < 1f) {
                        (clamps ??= []).Add(new(
                            PushInstructionIndex: push,
                            PopInstructionIndex: index,
                            InstanceIndex: instructionOwners[index],
                            ShapeCount: shapes,
                            StepScale: instruction.Data1.Y
                        ));
                    }
                    push = -1;
                    break;
            }
        }

        return clamps is null ? Array.Empty<SdfFieldScopeClamp>() : clamps.AsReadOnly();
    }
}
