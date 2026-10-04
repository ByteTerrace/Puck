namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // A pop's scale is known before its children in this reverse walk. Nested factors multiply along a path;
    // siblings take the maximum. The result also covers fields subsequently modified by a parent operation.
    private (float Rescale, int Depth) FieldScopeExtent(int first, int end) {
        Span<float> parents = stackalloc float[SdfProgramBuilder.MaxFieldScopeDepth];
        var depth = 0;
        var maximumDepth = 0;
        var current = 1f;
        var maximum = 1f;
        for (var index = end - 1; index >= first; index--) {
            var instruction = m_instructions[index];
            if (instruction.Op == SdfOp.PopField) {
                parents[depth++] = current;
                maximumDepth = Math.Max(maximumDepth, depth);
                if (instruction.Data1.Y > 0f) { current /= instruction.Data1.Y; }
                if (!float.IsFinite(current)) {
                    throw new InvalidOperationException("Nested field-scope distance rescaling exceeds the finite bound domain.");
                }
                maximum = MathF.Max(maximum, current);
            } else if (instruction.Op == SdfOp.PushField) {
                current = parents[--depth];
            }
        }
        return (maximum, maximumDepth);
    }
    // Each nesting level can add its own soft halo. Cover every level and every rescale path while retaining
    // the existing tight formula for flat scopes. This is a conservative geometric bound, not a march allowance.
    private float NestedFieldMarginScale(int first, int end) {
        var extent = FieldScopeExtent(first, end);
        var scale = extent.Depth > 1 ? extent.Depth * extent.Rescale : 1f;
        if (!float.IsFinite(scale)) {
            throw new InvalidOperationException("Nested field-scope margin scaling exceeds the finite bound domain.");
        }
        return scale;
    }

    // Read the already-baked stream, rather than maintaining a second version of the composition/warp analysis.
    // Validation has established balanced scopes and a single instruction owner throughout each scope.
    private IReadOnlyList<SdfFieldScopeClamp> ReadFieldScopeClamps(int[] instructionOwners) {
        List<SdfFieldScopeClamp>? clamps = null;
        Span<(int Push, int Shapes)> scopes = stackalloc (int, int)[SdfProgramBuilder.MaxFieldScopeDepth];
        var depth = 0;
        var shapes = 0;

        for (var index = 0; (index < m_instructions.Length); index++) {
            var instruction = m_instructions[index];

            switch (instruction.Op) {
                case SdfOp.PushField:
                    scopes[depth++] = (index, shapes);
                    break;
                case SdfOp.ShapeBlend:
                    shapes++;
                    break;
                case SdfOp.PopField:
                    var scope = scopes[--depth];
                    if (instruction.Data1.Y is > 0f and < 1f) {
                        (clamps ??= []).Add(item: new(
                            PushInstructionIndex: scope.Push,
                            PopInstructionIndex: index,
                            InstanceIndex: instructionOwners[index],
                            ShapeCount: (shapes - scope.Shapes),
                            StepScale: instruction.Data1.Y
                        ));
                    }
                    break;
            }
        }

        return ((clamps is null)
            ? Array.Empty<SdfFieldScopeClamp>()
            : clamps.AsReadOnly()
        );
    }
}
