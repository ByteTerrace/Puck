using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.Forge.Authoring;

/// <summary>
/// One SDF VM domain operator authored on a <see cref="ShapeDocument"/>, applied in CREATION space — after the
/// creation/placement frame chain (origin/rotation/scale) and BEFORE the shape's own translate/rotate/scale, so every
/// entry in <see cref="ShapeDocument.Domain"/> shares one space regardless of kind. This is the SDF VM's own domain
/// operator family (<see cref="SdfProgramBuilder"/>'s <c>SymmetryPlane</c>/<c>RepeatLimited</c>/<c>RepeatPolar</c>/
/// <c>WallpaperFold</c>) exposed as document data — a `$type`-discriminated ordered list — rather than one-off
/// boolean/record flags per capability (the retired <c>ShapeDocument.Mirror</c> and the standalone
/// <c>ShapeDocument.Wallpaper</c> member it replaces).
/// </summary>
[JsonDerivedType(typeof(Symmetry), typeDiscriminator: "symmetry")]
[JsonDerivedType(typeof(Repeat), typeDiscriminator: "repeat")]
[JsonDerivedType(typeof(Polar), typeDiscriminator: "polar")]
[JsonDerivedType(typeof(Wallpaper), typeDiscriminator: "wallpaper")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ShapeDomainOp {
    private ShapeDomainOp() {
    }

    /// <summary>Reflection fold across a plane — mirrors <see cref="SdfProgramBuilder.SymmetryPlane"/>. One authored
    /// half repeats mirror-imaged across the plane; an isometry, so BOTH render and the fixed-point solid field apply
    /// it (collision matches render).</summary>
    /// <param name="Normal">The plane normal, in the shape's creation-space frame (normalized at canonicalization;
    /// a zero/non-finite normal falls back to <see cref="Vector3.UnitX"/> — the retired <c>Mirror: true</c>
    /// flag's exact fold).</param>
    /// <param name="Offset">The plane's signed offset along <paramref name="Normal"/> (null = 0, through the
    /// creation origin).</param>
    public sealed record Symmetry(DocumentVector3 Normal, float? Offset = null) : ShapeDomainOp;
    /// <summary>Bounded linear domain repeat — mirrors <see cref="SdfProgramBuilder.RepeatLimited"/>. An isometry, so
    /// BOTH render and the fixed-point solid field apply it.</summary>
    /// <param name="Spacing">The per-axis cell spacing, creation units (clamped to >= 0.001 per axis, matching the
    /// builder's own floor).</param>
    /// <param name="Limit">The per-axis repeat-cell limit — the lattice spans cell indices -limit..+limit (null =
    /// <see cref="UnboundedLimit"/> per axis, far past any authored reach).</param>
    public sealed record Repeat(DocumentVector3 Spacing, DocumentVector3? Limit = null) : ShapeDomainOp {
        /// <summary>The per-axis repeat-cell limit an absent <see cref="Limit"/> means.</summary>
        public const float UnboundedLimit = 1000000f;
    }
    /// <summary>Angular domain repeat — mirrors <see cref="SdfProgramBuilder.RepeatPolar"/>. RENDER ONLY: the
    /// fixed-point solid field skips it — a solid placement authoring one gets its UNFOLDED (single-sector) geometry
    /// for contact, since the field evaluator's warp-free excluded-op rule has no fixed-point spelling for it.</summary>
    /// <param name="Count">The sector count around the axis (clamped >= 1).</param>
    /// <param name="Axis">The rotation axis (null = Y, the XZ ground plane).</param>
    /// <param name="Mirror">Whether adjacent sectors mirror across their shared bisector (null = false).</param>
    /// <param name="MaterialStride">The per-sector palette stride (null = 0, geometric only).</param>
    public sealed record Polar(int Count, SdfPolarAxis? Axis = null, bool? Mirror = null, int? MaterialStride = null) : ShapeDomainOp;
    /// <summary>Wallpaper-group lattice fold — mirrors <see cref="SdfProgramBuilder.WallpaperFold"/>. RENDER ONLY:
    /// the fixed-point solid field skips it — a solid placement authoring one gets its UNFOLDED geometry for contact,
    /// the same render-only posture the retired standalone <c>ShapeDocument.Wallpaper</c> member always carried.</summary>
    /// <param name="Group">The wallpaper group.</param>
    /// <param name="Cell">The lattice cell extents in the fold plane, creation units.</param>
    /// <param name="Limit">The repeat-cell limit per plane axis (null = <see cref="UnboundedLimit"/> per axis).</param>
    /// <param name="Plane">The fold plane (null = XZ).</param>
    /// <param name="MaterialStride">The parity-material stride (null = 0, geometric only).</param>
    /// <param name="LodDistance">The symmetry-LOD distance threshold (null = 0, off).</param>
    public sealed record Wallpaper(
        SdfWallpaperGroup Group,
        DocumentVector2 Cell,
        DocumentVector2? Limit = null,
        SdfWallpaperPlane? Plane = null,
        int? MaterialStride = null,
        float? LodDistance = null
    ) : ShapeDomainOp {
        /// <summary>The per-axis repeat-cell limit an absent <see cref="Limit"/> means: far past any authored reach.</summary>
        public const float UnboundedLimit = 1000000f;
    }
}
/// <summary>
/// Applies a shape's ordered <see cref="ShapeDomainOp"/> list to an <see cref="SdfProgramBuilder"/> chain — the ONE
/// place every emission path (the static placement stamper, the animated stamp pool, the fixed-point solid field)
/// reaches the SDF VM's domain-operator family from document data, so the four ops' argument mapping is written once.
/// </summary>
public static class ShapeDomainOps {
    /// <summary>A worst-case domain list — <see cref="ShapeDocument.MaxDomainOps"/> symmetry entries — for capacity
    /// probes: every domain op costs exactly one <c>SdfInstruction</c> regardless of kind, so probing with any single
    /// kind repeated to the cap covers the instruction-word cost of any real authored combination.</summary>
    public static readonly IReadOnlyList<ShapeDomainOp> ProbeWorstCase = BuildProbeWorstCase();

    private static IReadOnlyList<ShapeDomainOp> BuildProbeWorstCase() {
        var probe = new ShapeDomainOp[ShapeDocument.MaxDomainOps];

        for (var index = 0; (index < probe.Length); index++) {
            probe[index] = new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX);
        }

        return probe;
    }

    /// <summary>Applies every op in <paramref name="domain"/>, in authored order — the full family (render path).</summary>
    /// <param name="chain">The builder chain, already advanced past the creation/placement frame prefix.</param>
    /// <param name="domain">The shape's domain ops, or null/empty for no-op.</param>
    /// <returns><paramref name="chain"/>, for further chaining.</returns>
    public static SdfProgramBuilder Apply(SdfProgramBuilder chain, IReadOnlyList<ShapeDomainOp>? domain) {
        ArgumentNullException.ThrowIfNull(chain);

        if (domain is not { Count: > 0 } ops) {
            return chain;
        }

        foreach (var op in ops) {
            chain = ApplyOne(chain: chain, op: op);
        }

        return chain;
    }
    /// <summary>Applies only the ops the fixed-point solid field can interpret — <see cref="ShapeDomainOp.Symmetry"/>
    /// and <see cref="ShapeDomainOp.Repeat"/> — in authored order, silently skipping <see cref="ShapeDomainOp.Polar"/>
    /// and <see cref="ShapeDomainOp.Wallpaper"/> (render-only ops <see cref="Puck.SignedDistance.Queries.SdfFieldEvaluator"/>
    /// structurally cannot carry; a solid placement authoring one collides against its unfolded geometry).</summary>
    /// <param name="chain">The builder chain, already advanced past the creation/placement frame prefix.</param>
    /// <param name="domain">The shape's domain ops, or null/empty for no-op.</param>
    /// <returns><paramref name="chain"/>, for further chaining.</returns>
    public static SdfProgramBuilder ApplyFixedSupported(SdfProgramBuilder chain, IReadOnlyList<ShapeDomainOp>? domain) {
        ArgumentNullException.ThrowIfNull(chain);

        if (domain is not { Count: > 0 } ops) {
            return chain;
        }

        foreach (var op in ops) {
            if (
                (op is ShapeDomainOp.Symmetry) ||
                (op is ShapeDomainOp.Repeat)
            ) {
                chain = ApplyOne(chain: chain, op: op);
            }
        }

        return chain;
    }
    private static SdfProgramBuilder ApplyOne(SdfProgramBuilder chain, ShapeDomainOp op) {
        return op switch {
            ShapeDomainOp.Symmetry symmetry => chain.SymmetryPlane(
                normal: symmetry.Normal,
                offset: (symmetry.Offset ?? 0f)
            ),
            ShapeDomainOp.Repeat repeat => chain.RepeatLimited(
                spacing: repeat.Spacing,
                limit: (repeat.Limit ?? new Vector3(value: ShapeDomainOp.Repeat.UnboundedLimit))
            ),
            ShapeDomainOp.Polar polar => chain.RepeatPolar(
                count: polar.Count,
                axis: (polar.Axis ?? SdfPolarAxis.Y),
                mirror: (polar.Mirror ?? false),
                materialStride: (polar.MaterialStride ?? 0)
            ),
            ShapeDomainOp.Wallpaper wallpaper => chain.WallpaperFold(
                group: wallpaper.Group,
                cell: wallpaper.Cell,
                limit: (wallpaper.Limit ?? new Vector2(value: ShapeDomainOp.Wallpaper.UnboundedLimit)),
                plane: (wallpaper.Plane ?? SdfWallpaperPlane.XZ),
                materialStride: (wallpaper.MaterialStride ?? 0),
                lodDistance: (wallpaper.LodDistance ?? 0f)
            ),
            _ => throw new ArgumentOutOfRangeException(
                paramName: nameof(op),
                actualValue: op,
                message: "The shape domain op kind is not defined."
            ),
        };
    }
}
