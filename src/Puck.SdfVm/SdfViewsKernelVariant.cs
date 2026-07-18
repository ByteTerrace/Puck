namespace Puck.SdfVm;

/// <summary>
/// The compiled variants of the Stage 1 views kernel — the ONE enumerable pair, deliberately not a combinatorial
/// space. <see cref="Full"/> (sdf-world-views.comp) is the default and the bit-exact reference: the complete ISA,
/// every op and shape case compiled in. <see cref="CoreOps"/> (sdf-world-views-core.comp) compiles the exotic cases
/// OUT (the <c>SDF_CORE_OPS</c> strip in sdf-vm.hlsli), shrinking the interpreter's live register state so more warps
/// reside — the full interpreter is register-pressure-limited (~38% CS-warp occupancy, ~72% of the register file,
/// limited. <see cref="SdfWorldEngine.UploadProgram"/> selects per program via
/// <see cref="SdfViewsKernelVariants.Select"/>: a pure function of the instruction stream, so a program that touches
/// any stripped op/shape always runs <see cref="Full"/>, and under <see cref="CoreOps"/> every compiled-out case is
/// UNREACHABLE — the rendered field is semantically identical (a separate compiled binary can still carry the usual
/// DXC codegen-re-roll ±1 LSB noise class the calibrated threshold families encode, never a structural change). Only
/// the VIEWS kernel has a core variant — stripping the beam REGRESSED its cone march (a register-allocation/
/// scheduling shift), so it stays on the full interpreter.
/// </summary>
public enum SdfViewsKernelVariant {
    /// <summary>The complete ISA (sdf-world-views.comp) — the default and the bit-exact reference.</summary>
    Full = 0,
    /// <summary>The exotic-ops-stripped interpreter (sdf-world-views-core.comp) — selected only for a program whose
    /// instruction stream provably touches no stripped op or shape.</summary>
    CoreOps = 1,
}

/// <summary>Selects the Stage 1 views kernel variant for a program — the host half of the <c>SDF_CORE_OPS</c>
/// contract. KEEP the exotic sets IN SYNC with the <c>#ifndef SDF_CORE_OPS</c> strips in sdf-vm.hlsli: an op or shape
/// stripped there must answer <see cref="SdfViewsKernelVariant.Full"/> here, or the core variant silently no-ops it.</summary>
public static class SdfViewsKernelVariants {
    /// <summary>Selects the views kernel variant for <paramref name="program"/> — <see cref="SdfViewsKernelVariant.CoreOps"/>
    /// exactly when <see cref="FirstExoticTouch"/> finds nothing. Deterministic and data-driven: the same program
    /// always selects the same variant on every backend.</summary>
    /// <param name="program">The program about to be uploaded.</param>
    /// <returns>The variant the engine should dispatch Stage 1 with.</returns>
    public static SdfViewsKernelVariant Select(SdfProgram program) =>
        ((FirstExoticTouch(program: program) is null) ? SdfViewsKernelVariant.CoreOps : SdfViewsKernelVariant.Full);

    /// <summary>The first exotic op/shape the program's instruction stream touches (a human-readable name for the
    /// selection log), or <see langword="null"/> when the whole stream is core — the histogram walk behind
    /// <see cref="Select"/>.</summary>
    /// <param name="program">The program to inspect.</param>
    /// <returns>The first exotic touch (e.g. <c>"op TwistY"</c> or <c>"shape Torus"</c>), or <see langword="null"/>.</returns>
    public static string? FirstExoticTouch(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);

        foreach (var instruction in program.Instructions) {
            switch (instruction.Op) {
                case SdfOp.ResetPoint:
                case SdfOp.Translate:
                case SdfOp.Rotate:
                case SdfOp.Scale:
                case SdfOp.TransformDynamic:
                    break;
                case SdfOp.ShapeBlend:
                    switch ((SdfShapeType)instruction.Shape) {
                        case SdfShapeType.Box:
                        case SdfShapeType.Capsule:
                        case SdfShapeType.Cylinder:
                        case SdfShapeType.Sphere:
                        case SdfShapeType.Plane:
                        case SdfShapeType.RoundedRectangle:
                        case SdfShapeType.ScreenSlab:
                        case SdfShapeType.Glyph:
                        // A SampledRegion (baked carve brick) is compiled into BOTH views variants (its evaluateShape
                        // case is OUTSIDE the SDF_CORE_OPS strip in sdf-vm.hlsli), so a baked carve scene stays on the
                        // faster core-ops interpreter — the whole point of collapsing O(carve-count) to one O(1) brick.
                        case SdfShapeType.SampledRegion:
                            break;
                        default:
                            return $"shape {(SdfShapeType)instruction.Shape}";
                    }

                    break;
                default:
                    return $"op {instruction.Op}";
            }
        }

        return null;
    }
}
