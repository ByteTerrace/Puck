using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The three compiled variants of the views shading kernel. <see cref="Full"/> (sdf-world-views.comp) is the default
/// reference: the complete ISA,
/// every op and shape case compiled in. <see cref="CoreOps"/> (sdf-world-views-core.comp) compiles the exotic cases
/// out (the <c>SDF_CORE_OPS</c> strip in sdf-vm.hlsli), reducing shader size and live register state. Occupancy and
/// performance depend on the scene, shader build, and device; a previous fixture's counters are not a universal limit.
/// <see cref="SdfWorldEngine.UploadProgram"/> selects per program via
/// <see cref="SdfViewsKernelVariants.Select"/>: a pure function of the instruction stream, so a program that touches
/// a heavy op/shape runs <see cref="Full"/>, the middle tier runs <see cref="Folds"/>, and under <see cref="CoreOps"/> every compiled-out case is
/// unreachable — the rendered field is semantically identical (a separate compiled binary can still carry the usual
/// DXC codegen-re-roll ±1 LSB noise class the calibrated threshold families encode, never a structural change). Only
/// the views kernel has stripped variants. Primary traversal keeps the full interpreter; an earlier beam-stripping
/// experiment regressed its cone march, so the beam also keeps the full interpreter.
/// </summary>
public enum SdfViewsKernelVariant {
    /// <summary>The complete ISA shading kernel (sdf-world-views.comp), used as the variant reference.</summary>
    Full = 0,
    /// <summary>The exotic-ops-stripped interpreter (sdf-world-views-core.comp) — selected only for a program whose
    /// instruction stream provably touches no stripped op or shape.</summary>
    CoreOps = 1,
    /// <summary>The fold-ops middle tier (sdf-world-views-folds.comp) — folds, scopes, and the simple exotic shapes
    /// stay compiled; the HEAVY warp/noise family (<c>SDF_STRIP_HEAVY</c> in sdf-vm.hlsli) compiles out. Selected for
    /// a program that touches a fold/scope/simple-exotic case but no heavy one.</summary>
    Folds = 2,
}
/// <summary>Selects the Stage 1 views kernel variant for a program — the host half of the <c>SDF_CORE_OPS</c>
/// contract. KEEP the exotic sets IN SYNC with the <c>#ifndef SDF_CORE_OPS</c> strips in sdf-vm.hlsli: an op or shape
/// stripped there must answer <see cref="SdfViewsKernelVariant.Full"/> here, or the core variant silently no-ops it.</summary>
public static class SdfViewsKernelVariants {
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
                        // ChamferedRectangle's evaluateShape case sits outside both strip guards in sdf-vm.hlsli (a CORE
                        // shape beside RoundedRectangle), so a chamfered program stays on the core-ops interpreter.
                        case SdfShapeType.ChamferedRectangle:
                        case SdfShapeType.ScreenSlab:
                        case SdfShapeType.Glyph:
                        // A SampledRegion (baked carve brick) is compiled into BOTH views variants (its evaluateShape
                        // case is OUTSIDE the SDF_CORE_OPS strip in sdf-vm.hlsli), so a baked carve scene stays on the
                        // faster core-ops interpreter — the whole point of collapsing O(carve-count) to one O(1) brick.
                        case SdfShapeType.SampledRegion:
                            break;
                        default:
                            return $"shape {((SdfShapeType)instruction.Shape)}";
                    }

                    break;
                default:
                    return $"op {instruction.Op}";
            }
        }

        return null;
    }
    /// <summary>The first HEAVY op/shape the program's instruction stream touches (the <c>SDF_STRIP_HEAVY</c> set —
    /// the warp/noise family and the analytic-solve 2D shapes), or <see langword="null"/> when it touches none. KEEP
    /// this set IN SYNC with the <c>SDF_STRIP_HEAVY</c> gates in sdf-vm.hlsli.</summary>
    /// <param name="program">The program to inspect.</param>
    /// <returns>The first heavy touch, or <see langword="null"/>.</returns>
    public static string? FirstHeavyTouch(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);

        foreach (var instruction in program.Instructions) {
            switch (instruction.Op) {
                case SdfOp.RotatePlane:
                case SdfOp.Shear:
                case SdfOp.GaussianPush:
                case SdfOp.LogSphere:
                case SdfOp.CellJitter:
                case SdfOp.Displace:
                case SdfOp.DomainWarp:
                case SdfOp.NoiseDisplace:
                case SdfOp.CellDisplace:
                case SdfOp.AxialProfile:
                case SdfOp.LaneErode:
                    return $"op {instruction.Op}";
                case SdfOp.ShapeBlend:
                    switch ((SdfShapeType)instruction.Shape) {
                        case SdfShapeType.RegularPolygon:
                        case SdfShapeType.Star:
                        case SdfShapeType.Trapezoid:
                        case SdfShapeType.Ellipse:
                        case SdfShapeType.Superellipsoid:
                        case SdfShapeType.ConvexPolygon:
                        case SdfShapeType.Sweep:
                            return $"shape {((SdfShapeType)instruction.Shape)}";
                        default:
                            break;
                    }

                    break;
                default:
                    break;
            }
        }

        return null;
    }
    /// <summary>Selects the views kernel variant for <paramref name="program"/>: <see cref="SdfViewsKernelVariant.Full"/>
    /// when the stream touches a heavy op/shape, <see cref="SdfViewsKernelVariant.Folds"/> when it touches only the
    /// fold/scope/simple-exotic tier, else <see cref="SdfViewsKernelVariant.CoreOps"/>. Deterministic and
    /// data-driven: the same program always selects the same variant on every backend.</summary>
    /// <param name="program">The program about to be uploaded.</param>
    /// <returns>The variant the engine should dispatch Stage 1 with, and the touch that decided it (null for core).</returns>
    public static (SdfViewsKernelVariant Variant, string? Touch) Select(SdfProgram program) {
        if (FirstHeavyTouch(program: program) is { } heavy) {
            return (SdfViewsKernelVariant.Full, heavy);
        }

        if (FirstExoticTouch(program: program) is { } exotic) {
            return (SdfViewsKernelVariant.Folds, exotic);
        }

        return (SdfViewsKernelVariant.CoreOps, null);
    }
}
