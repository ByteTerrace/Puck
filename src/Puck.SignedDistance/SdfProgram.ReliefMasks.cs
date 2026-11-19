namespace Puck.SignedDistance;

// A relief mask (SdfProgramBuilder.BeginReliefMask): a field scope seeded at its band's end, holding a region's union
// shapes and closed by one ReliefWeight, which multiplies a per-sample weight in [0, 1] the next NoiseDisplace in the
// enclosing scope consumes. Three facts hold every reader of the stream sound, and this file is where the program
// enforces them, the one door every program passes:
//   1. The weight lies in [0, 1], so a masked relief reaches no farther than its amplitude: every channel that reads
//      the relief's reach as its amplitude (the cull's relief charge, the interval rule, the instance bound, contact's
//      growth) stays sound unchanged.
//   2. The weight reaches only the relief that follows its masks: after a mask's pop, only further mask scopes, point
//      ops and then that relief may stand in the enclosing scope.
//   3. A mask scope composes nothing: its ReliefWeight is its last instruction and leaves the far distance, which its
//      union pop composes as nothing.
public sealed partial class SdfProgram {
    /// <summary>Returns whether <paramref name="instruction"/> opens a relief mask's scope: a
    /// <see cref="SdfOp.PushField"/> whose Shape lane carries a seed (<see cref="SdfProgramBuilder.BeginReliefMask"/>).</summary>
    /// <param name="instruction">The instruction.</param>
    /// <returns><see langword="true"/> for a seeded push.</returns>
    public static bool IsReliefMaskPush(SdfInstruction instruction) => IsReliefMaskPush(op: instruction.Op, shape: instruction.Shape);
    /// <summary>Returns whether an instruction of <paramref name="op"/> whose Shape lane holds <paramref name="shape"/>
    /// opens a relief mask's scope (<see cref="IsReliefMaskPush(SdfInstruction)"/>).</summary>
    /// <param name="op">The instruction's op.</param>
    /// <param name="shape">The instruction's Shape lane.</param>
    /// <returns><see langword="true"/> for a seeded push.</returns>
    public static bool IsReliefMaskPush(SdfOp op, uint shape) => ((op == SdfOp.PushField) && (shape != 0u));
    /// <summary>Returns the most a relief mask's weight changes per world unit of distance from its region: smoothstep's
    /// largest slope, 1.5 per band width, times the weight's span. A masked relief's gradient is
    /// <c>a·(w∇n + n∇w)</c> with <c>|n| ≤ 1</c> and <c>w ≤ 1</c>, so each mask adds this slope times the relief's
    /// amplitude to its step bound (<see cref="NoiseDisplaceStepFactor"/>).</summary>
    /// <param name="fade">The band's width, in world units, above zero.</param>
    /// <param name="inside">The weight on and inside the region.</param>
    /// <param name="outside">The weight past the band.</param>
    /// <returns>The slope, per world unit.</returns>
    public static float ReliefMaskSlope(float fade, float inside, float outside) => ((MathF.Abs(x: (outside - inside)) * 1.5f) / fade);

    // Per instruction, the summed slope (ReliefMaskSlope) of the masks a NoiseDisplace consumes; null for a stream with no
    // mask, so a mask-free program allocates nothing. RequireReliefMasks guarantees each mask reaches exactly the next
    // relief at its own depth.
    private static float[]? ReliefMaskSlopes(SdfInstruction[] instructions) {
        float[]? slopes = null;
        Span<float> pending = stackalloc float[(SdfProgramBuilder.MaxFieldScopeDepth + 1)];
        var depth = 0;

        pending.Clear();
        for (var index = 0; (index < instructions.Length); index++) {
            var instruction = instructions[index];

            switch (instruction.Op) {
                case SdfOp.PushField: {
                        depth++;
                        break;
                    }
                case SdfOp.PopField: {
                        depth--;
                        break;
                    }
                case SdfOp.ReliefWeight: {
                        pending[(depth - 1)] += ReliefMaskSlope(fade: instruction.Data0.Y, inside: instruction.Data0.Z, outside: instruction.Data0.W);
                        break;
                    }
                case SdfOp.NoiseDisplace: {
                        if (pending[depth] > 0f) {
                            (slopes ??= new float[instructions.Length])[index] = pending[depth];
                            pending[depth] = 0f;
                        }

                        break;
                    }
                default: {
                        break;
                    }
            }
        }

        return slopes;
    }
    // Refuses, by name, every relief mask stream the three facts above do not hold for: a weight outside [0, 1] or a band
    // that is not finite and wide; a seeded scope that is not a mask (anything but point ops and union shapes before one
    // closing ReliefWeight, which its union pop with no smooth radius follows); a ReliefWeight outside such a scope; and a
    // mask that anything but further masks, point ops and its relief follows in its enclosing scope, or that no relief
    // follows. Runs after RequireBalancedFieldScopes, so scopes nest and close.
    private void RequireReliefMasks(string paramName) {
        Span<bool> pending = stackalloc bool[(SdfProgramBuilder.MaxFieldScopeDepth + 1)];
        var depth = 0;
        var maskDepth = 0;
        var closed = false;

        pending.Clear();
        for (var index = 0; (index < m_instructions.Length); index++) {
            var instruction = m_instructions[index];
            var op = instruction.Op;

            if (maskDepth > 0) {
                if (op == SdfOp.ReliefWeight) {
                    RequireReliefBand(index: index, instruction: instruction, paramName: paramName);
                    var pop = (((index + 1) < m_instructions.Length) ? m_instructions[(index + 1)] : default);

                    if ((pop.Op != SdfOp.PopField) || (pop.Blend != ((uint)SdfBlendOp.Union)) || (pop.Data1.X != 0f)) {
                        throw new ArgumentException(message: $"Relief mask weight {index} must be the last instruction of its scope, closed by a union pop with no smooth radius.", paramName: paramName);
                    }

                    closed = true;
                    continue;
                }
                if (op == SdfOp.PopField) {
                    if (!closed) {
                        throw new ArgumentException(message: $"The relief mask scope closing at instruction {index} holds no ReliefWeight before its pop.", paramName: paramName);
                    }

                    depth--;
                    maskDepth = 0;
                    pending[depth] = true;
                    continue;
                }
                if (closed || (op == SdfOp.PushField) || ((op == SdfOp.ShapeBlend) && ((instruction.Blend != ((uint)SdfBlendOp.Union)) || (instruction.Data1.X != 0f))) || ((op != SdfOp.ShapeBlend) && (op != SdfOp.ResetPoint) && (SdfOpRoles.Of(op: op) is not (SdfOpRole.Point or SdfOpRole.Lattice)))) {
                    throw new ArgumentException(message: $"Instruction {index} ({op}) cannot stand in a relief mask, whose region is point ops and union shapes alone, closed by one ReliefWeight.", paramName: paramName);
                }

                continue;
            }

            if (op == SdfOp.ReliefWeight) {
                throw new ArgumentException(message: $"Relief mask weight {index} stands outside a relief mask's seeded scope.", paramName: paramName);
            }

            if (pending[depth]) {
                if (op == SdfOp.NoiseDisplace) {
                    pending[depth] = false;
                } else if (!((op == SdfOp.ResetPoint) || IsReliefMaskPush(instruction: instruction) || ((op != SdfOp.ShapeBlend) && (op != SdfOp.PushField) && (op != SdfOp.PopField) && (SdfOpRoles.Of(op: op) is SdfOpRole.Point or SdfOpRole.Lattice)))) {
                    throw new ArgumentException(message: $"Instruction {index} ({op}) follows a relief mask in its scope, where only further masks, point ops and the relief the masks weight may stand.", paramName: paramName);
                }
            }

            if (op == SdfOp.PushField) {
                depth++;
                if (IsReliefMaskPush(instruction: instruction)) {
                    var seed = BitConverter.UInt32BitsToSingle(value: instruction.Shape);

                    if (!(float.IsFinite(f: seed) && (seed > 0f))) {
                        throw new ArgumentException(message: $"Relief mask scope {index} is seeded at {seed}; a mask's seed is its band's end, finite and above zero.", paramName: paramName);
                    }

                    maskDepth = depth;
                    closed = false;
                }
            } else if (op == SdfOp.PopField) {
                depth--;
            }
        }

        for (var level = 0; (level < pending.Length); level++) {
            if (pending[level]) {
                throw new ArgumentException(message: "A relief mask closes with no relief following it in its scope; a mask weights the next NoiseDisplace.", paramName: paramName);
            }
        }
    }
    // A relief mask's band and weights (Data0 = start, fade, inside, outside) and its baked 1/L (Data1.x).
    private static void RequireReliefBand(int index, SdfInstruction instruction, string paramName) {
        var (start, fade, inside, outside, scale) = (instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z, instruction.Data0.W, instruction.Data1.X);

        if (!(float.IsFinite(f: start) && float.IsFinite(f: fade) && (fade > 0f))) {
            throw new ArgumentException(message: $"Relief mask weight {index} has band start {start} and width {fade}; the start must be finite and the width finite and above zero.", paramName: paramName);
        }
        if (!((inside >= 0f) && (inside <= 1f) && (outside >= 0f) && (outside <= 1f))) {
            throw new ArgumentException(message: $"Relief mask weight {index} has weights {inside} inside and {outside} outside; both must lie in [0, 1], so a mask only lowers relief.", paramName: paramName);
        }
        if (!((scale > 0f) && (scale <= 1f))) {
            throw new ArgumentException(message: $"Relief mask weight {index} carries a region scale of {scale}; its baked 1/L lies in (0, 1].", paramName: paramName);
        }
    }
}
