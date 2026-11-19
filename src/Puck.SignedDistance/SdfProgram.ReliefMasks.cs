namespace Puck.SignedDistance;

// A relief mask (SdfProgramBuilder.BeginReliefMask): a field scope seeded at its band's end, holding a region's union
// shapes and closed by one ReliefWeight and a canonical pop, which multiplies a per-sample weight in [0, 1] the next
// NoiseDisplace in the enclosing scope consumes. Two doors hold every reader of the stream sound:
//
// THE ADMISSION DOOR (RequireReliefMasks, run on every program before any analysis, and AnalyzeLipschitz's ReliefWeight
// arm, the one writer of the derived words) holds:
//   1. The weight lies in [0, 1], so a masked relief reaches no farther than its amplitude, and every channel that reads
//      the relief's reach as its amplitude stays sound.
//   2. The band's endpoints in the region field's own units (ReliefWeight's Data1.x and Data1.y) and the push's seed are
//      derived together from the band and the region's bound, never taken from the caller: AnalyzeLipschitz overwrites
//      all three from (start, fade) and L, and refuses a non-finite result.
//   3. The region field is continuous and contact-readable: a mask admits rigid point ops, mirrors and elongation alone
//      (no fold that jumps at a seam, no warp), and hard-union shapes with no detail flag and no plane.
//   4. A mask scope composes nothing: its ReliefWeight leaves the far distance and its pop is canonical (a union with no
//      smooth radius and no candidate scale, all data zero), so the parent's field is restored as it was.
//   5. The weight reaches only the relief that follows its masks, in the same scope and instruction owner, which starts
//      its own chain: after a mask only further masks, point ops and a ResetPoint may precede that relief.
//
// THE SHARED READING (ReliefMaskInstructions): every op-stream walker that asks "which shapes are surfaces" (the audit's
// shape and member extraction, glass classification, the world group's drawn test) reads the one per-instruction table
// of mask scopes rather than re-deriving it. The interpreters need no table: by (4) a mask scope composes nothing on its
// own, and the cull's certificate steps over it (TryCertifyInstance).
public sealed partial class SdfProgram {
    // The ops a relief mask's region may apply to its point: the rigid moves, a mirror and elongation, each continuous.
    private static readonly HashSet<SdfOp> ReliefMaskPointOps = [SdfOp.ResetPoint, SdfOp.Translate, SdfOp.Rotate, SdfOp.Scale, SdfOp.TransformDynamic, SdfOp.SymmetryPlane, SdfOp.Elongate];

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
    /// <summary>Returns, per instruction of <paramref name="instructions"/>, whether it stands in a relief mask's scope,
    /// its seeded push and canonical pop included: the one reading of which instructions draw a region, never a surface.
    /// <see langword="null"/> for a stream with no mask, so a mask-free walk allocates nothing.</summary>
    /// <param name="instructions">The instruction stream, admitted by the program's validation.</param>
    /// <returns>The table, or <see langword="null"/>.</returns>
    public static bool[]? ReliefMaskInstructions(IReadOnlyList<SdfInstruction> instructions) {
        ArgumentNullException.ThrowIfNull(argument: instructions);

        bool[]? inside = null;
        var open = false;

        for (var index = 0; (index < instructions.Count); index++) {
            var instruction = instructions[index];

            if (IsReliefMaskPush(instruction: instruction)) { open = true; }
            if (open) { (inside ??= new bool[instructions.Count])[index] = true; }
            if (open && (instruction.Op == SdfOp.PopField)) { open = false; }
        }

        return inside;
    }
    // This program's mask table (ReliefMaskInstructions), read once; null for a program with no mask.
    private bool[]? ReliefMasks => (m_reliefMasks ??= (ReliefMaskInstructions(instructions: m_instructions) ?? []));
    private bool[]? m_reliefMasks;
    /// <summary>Returns whether this program's instruction at <paramref name="index"/> is a shape that draws a surface
    /// (<see cref="IsSurfaceShape(IReadOnlyList{SdfInstruction}, bool[], int)"/>).</summary>
    /// <param name="index">The instruction's index.</param>
    /// <returns><see langword="true"/> for a surface shape.</returns>
    public bool IsSurfaceShape(int index) => IsSurfaceShape(index: index, instructions: m_instructions, masks: ((ReliefMasks is { Length: > 0 } masks) ? masks : null));
    /// <summary>Returns whether the instruction at <paramref name="index"/> is a shape that draws a surface: a
    /// <see cref="SdfOp.ShapeBlend"/> outside every relief mask (<see cref="ReliefMaskInstructions"/>).</summary>
    /// <param name="instructions">The instruction stream.</param>
    /// <param name="masks">The stream's mask table, or <see langword="null"/> for none.</param>
    /// <param name="index">The instruction's index.</param>
    /// <returns><see langword="true"/> for a surface shape.</returns>
    public static bool IsSurfaceShape(IReadOnlyList<SdfInstruction> instructions, bool[]? masks, int index) =>
        ((instructions[index].Op == SdfOp.ShapeBlend) && ((masks is null) || !masks[index]));
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
    // mask. Admission guarantees each mask reaches exactly the next relief at its own depth.
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
    // The admission door's validation half (facts 1, 3, 4 and 5 above; AnalyzeLipschitz writes fact 2's words). Runs after
    // RequireBalancedFieldScopes, so scopes nest, close and stay inside one owner.
    private void RequireReliefMasks(int[] instructionOwners, string paramName) {
        Span<bool> pending = stackalloc bool[(SdfProgramBuilder.MaxFieldScopeDepth + 1)];
        var depth = 0;
        var maskDepth = 0;
        var closed = false;
        var pendingOwner = 0;
        var reset = false;
        var anyPending = false;

        pending.Clear();
        for (var index = 0; (index < m_instructions.Length); index++) {
            var instruction = m_instructions[index];
            var op = instruction.Op;

            if (anyPending && (instructionOwners[index] != pendingOwner)) {
                throw new ArgumentException(message: $"Instruction {index} leaves the owner of a relief mask whose relief has not followed; a mask and the relief it weights share one owner.", paramName: paramName);
            }

            if (maskDepth > 0) {
                if (op == SdfOp.ReliefWeight) {
                    RequireReliefBand(index: index, instruction: instruction, paramName: paramName);

                    var pop = (((index + 1) < m_instructions.Length) ? m_instructions[(index + 1)] : default);

                    if ((pop.Op != SdfOp.PopField) || (pop.Blend != ((uint)SdfBlendOp.Union)) || (pop.Data0 != default) || (pop.Data1 != default)) {
                        throw new ArgumentException(message: $"Relief mask weight {index} must be the last instruction of its scope, closed by a canonical pop: a union with every data lane zero.", paramName: paramName);
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
                    anyPending = true;
                    pendingOwner = instructionOwners[index];
                    reset = false;
                    continue;
                }

                var admitted = (op == SdfOp.ShapeBlend)
                    ? ((instruction.Blend == ((uint)SdfBlendOp.Union)) && (instruction.Data1.X == 0f) && !instruction.Detail && (instruction.Shape != ((uint)SdfShapeType.Plane)))
                    : ReliefMaskPointOps.Contains(item: op);

                if (closed || !admitted) {
                    throw new ArgumentException(message: $"Instruction {index} ({op}) cannot stand in a relief mask, whose region is rigid point ops, mirrors, elongation and bounded union shapes with no detail flag, closed by one ReliefWeight.", paramName: paramName);
                }

                continue;
            }

            if (op == SdfOp.ReliefWeight) {
                throw new ArgumentException(message: $"Relief mask weight {index} stands outside a relief mask's seeded scope.", paramName: paramName);
            }

            if (pending[depth]) {
                if (op == SdfOp.NoiseDisplace) {
                    if (!reset) {
                        throw new ArgumentException(message: $"Relief {index} weighted by relief masks must start its own chain with a ResetPoint after the last mask.", paramName: paramName);
                    }

                    pending[depth] = false;
                    anyPending = pending.Contains(value: true);
                } else if (op == SdfOp.ResetPoint) {
                    reset = true;
                } else if (!(IsReliefMaskPush(instruction: instruction) || ((op != SdfOp.ShapeBlend) && (op != SdfOp.PushField) && (op != SdfOp.PopField) && (SdfOpRoles.Of(op: op) is SdfOpRole.Point or SdfOpRole.Lattice)))) {
                    throw new ArgumentException(message: $"Instruction {index} ({op}) follows a relief mask in its scope, where only further masks, point ops and the relief the masks weight may stand.", paramName: paramName);
                }
            }

            if (op == SdfOp.PushField) {
                depth++;
                if (IsReliefMaskPush(instruction: instruction)) {
                    maskDepth = depth;
                    closed = false;
                }
            } else if (op == SdfOp.PopField) {
                depth--;
            }
        }

        if (anyPending) {
            throw new ArgumentException(message: "A relief mask closes with no relief following it in its scope; a mask weights the next NoiseDisplace.", paramName: paramName);
        }
    }
    // A relief mask's band and weights (Data0 = start, fade, inside, outside): finite, a band ending beyond the region's
    // surface, the weights in [0, 1].
    private static void RequireReliefBand(int index, SdfInstruction instruction, string paramName) {
        var (start, fade, inside, outside) = (instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z, instruction.Data0.W);

        if (!(float.IsFinite(f: start) && float.IsFinite(f: fade) && (fade > 0f) && float.IsFinite(f: (start + fade)) && ((start + fade) > 0f))) {
            throw new ArgumentException(message: $"Relief mask weight {index} has band start {start} and width {fade}; both must be finite, the width above zero and the band's end finite and beyond the region's surface.", paramName: paramName);
        }
        if (!((inside >= 0f) && (inside <= 1f) && (outside >= 0f) && (outside <= 1f))) {
            throw new ArgumentException(message: $"Relief mask weight {index} has weights {inside} inside and {outside} outside; both must lie in [0, 1], so a mask only lowers relief.", paramName: paramName);
        }
    }
    // Fact 2: the band's endpoints in the region field's units, from the band and the region's bound L, and the seed equal to
    // the end. The one writer of these words; a non-finite result is refused.
    private static (float Start, float End) ReliefMaskEndpoints(SdfInstruction weight, float regionLipschitz, int index) {
        var start = (weight.Data0.X * regionLipschitz);
        var end = ((weight.Data0.X + weight.Data0.Y) * regionLipschitz);

        if (!(float.IsFinite(f: start) && float.IsFinite(f: end) && (end > start) && (end > 0f))) {
            throw new InvalidOperationException(message: $"Relief mask weight {index}'s band times its region's bound {regionLipschitz} leaves the finite range ({start}, {end}).");
        }

        return (start, end);
    }
}
