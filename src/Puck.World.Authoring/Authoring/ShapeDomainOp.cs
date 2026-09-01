using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>
/// One domain operator authored on a <see cref="ShapeDocument"/>, applied in creation space — after the
/// creation/placement frame chain (origin/rotation/scale) and before the shape's own translate/rotate/scale, so every
/// entry in <see cref="ShapeDocument.Domain"/> shares one space regardless of kind. The wire form of
/// <see cref="SdfDomainOp"/>: a <c>$type</c>-discriminated ordered list whose optional members
/// <see cref="ShapeDomainOps.ToDomainOps"/> resolves.
/// </summary>
/// <remarks>The render path takes these as point folds; the contact paths take them as the rigid copies
/// <see cref="SdfDomainExpansion"/> derives, so collision matches render for every op that expands. An op that does
/// not expand is refused on a solid placement by the world validator, naming it.</remarks>
[JsonDerivedType(typeof(Symmetry), typeDiscriminator: "symmetry")]
[JsonDerivedType(typeof(Repeat), typeDiscriminator: "repeat")]
[JsonDerivedType(typeof(Polar), typeDiscriminator: "polar")]
[JsonDerivedType(typeof(Wallpaper), typeDiscriminator: "wallpaper")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ShapeDomainOp {
    private ShapeDomainOp() {
    }

    /// <summary>Reflection fold across a plane — <see cref="SdfDomainOp.Symmetry"/>. One authored half repeats
    /// mirror-imaged across the plane.</summary>
    /// <param name="Normal">The plane normal, in the shape's creation-space frame (normalized at canonicalization;
    /// a zero/non-finite normal falls back to <see cref="Vector3.UnitX"/> — the retired <c>Mirror: true</c>
    /// flag's exact fold).</param>
    /// <param name="Offset">The plane's signed offset along <paramref name="Normal"/> (null = 0, through the
    /// creation origin).</param>
    public sealed record Symmetry(DocumentVector3 Normal, float? Offset = null) : ShapeDomainOp;
    /// <summary>Bounded linear domain repeat — <see cref="SdfDomainOp.Repeat"/>. Expands for contact only when the
    /// limit is a whole number within the copy budget; an absent limit is <see cref="Repeat.UnboundedLimit"/> and does
    /// not expand.</summary>
    /// <param name="Spacing">The per-axis cell spacing, creation units (clamped to >= 0.001 per axis, matching the
    /// builder's own floor).</param>
    /// <param name="Limit">The per-axis repeat-cell limit — the lattice spans cell indices -limit..+limit (null =
    /// <see cref="UnboundedLimit"/> per axis, far past any authored reach).</param>
    public sealed record Repeat(DocumentVector3 Spacing, DocumentVector3? Limit = null) : ShapeDomainOp {
        /// <summary>The per-axis repeat-cell limit an absent <see cref="Limit"/> means.</summary>
        public const float UnboundedLimit = 1000000f;
    }
    /// <summary>Angular domain repeat — <see cref="SdfDomainOp.Polar"/>. Its sectors expand to one rigid copy each,
    /// so contact carries the full ring.</summary>
    /// <param name="Count">The sector count around the axis (clamped >= 1).</param>
    /// <param name="Axis">The rotation axis (null = Y, the XZ ground plane).</param>
    /// <param name="Mirror">Whether adjacent sectors mirror across their shared bisector (null = false).</param>
    /// <param name="MaterialStride">The per-sector palette stride (null = 0, geometric only).</param>
    public sealed record Polar(int Count, SdfPolarAxis? Axis = null, bool? Mirror = null, int? MaterialStride = null) : ShapeDomainOp;
    /// <summary>Wallpaper-group lattice fold — <see cref="SdfDomainOp.Wallpaper"/>. Render only: it has no rigid-copy
    /// expansion, so a solid placement carrying one is refused by name at validation.</summary>
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
/// Maps a shape's authored <see cref="ShapeDomainOp"/> list onto the SDF VM's own <see cref="SdfDomainOp"/> vocabulary
/// — the one place the document family's optional-member defaults are resolved, so no emission path decides them
/// twice.
/// </summary>
/// <remarks>Every meaning past this boundary belongs to <see cref="SdfDomainOps"/> (the fold) and
/// <see cref="SdfDomainExpansion"/> (the copies); this type only reads the document.</remarks>
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
    private static SdfDomainOp Map(ShapeDomainOp op) {
        return op switch {
            ShapeDomainOp.Symmetry symmetry => new SdfDomainOp.Symmetry(
                Normal: symmetry.Normal,
                Offset: (symmetry.Offset ?? 0f)
            ),
            ShapeDomainOp.Repeat repeat => new SdfDomainOp.Repeat(
                Limit: (repeat.Limit ?? new Vector3(value: ShapeDomainOp.Repeat.UnboundedLimit)),
                Spacing: repeat.Spacing
            ),
            ShapeDomainOp.Polar polar => new SdfDomainOp.Polar(
                Axis: (polar.Axis ?? SdfPolarAxis.Y),
                Count: polar.Count,
                MaterialStride: (polar.MaterialStride ?? 0),
                Mirror: (polar.Mirror ?? false)
            ),
            ShapeDomainOp.Wallpaper wallpaper => new SdfDomainOp.Wallpaper(
                Cell: wallpaper.Cell,
                Group: wallpaper.Group,
                Limit: (wallpaper.Limit ?? new Vector2(value: ShapeDomainOp.Wallpaper.UnboundedLimit)),
                LodDistance: (wallpaper.LodDistance ?? 0f),
                MaterialStride: (wallpaper.MaterialStride ?? 0),
                Plane: (wallpaper.Plane ?? SdfWallpaperPlane.XZ)
            ),
            _ => throw new ArgumentOutOfRangeException(
                paramName: nameof(op),
                actualValue: op,
                message: "The shape domain op kind is not defined."
            ),
        };
    }

    /// <summary>Applies every op in <paramref name="domain"/>, in authored order, as point folds.</summary>
    /// <param name="chain">The builder chain, already advanced past the creation/placement frame prefix.</param>
    /// <param name="domain">The shape's domain ops, or null/empty for no-op.</param>
    /// <returns><paramref name="chain"/>, for further chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chain"/> is <see langword="null"/>.</exception>
    public static SdfProgramBuilder Apply(SdfProgramBuilder chain, IReadOnlyList<ShapeDomainOp>? domain) {
        ArgumentNullException.ThrowIfNull(chain);

        if (domain is not { Count: > 0 }) {
            return chain;
        }

        return SdfDomainOps.Apply(
            chain: chain,
            domain: ToDomainOps(domain: domain)
        );
    }
    /// <summary>Returns a conservative bound on how far a shape's domain ops can carry its geometry from the
    /// creation origin, in creation units — the term a render bound adds so a folded lattice is not culled down to
    /// the un-folded shape's own sphere. Ops compose, so each op's displacement bound is summed: a symmetry plane
    /// through the origin and a polar fold are origin-preserving isometries (0); an offset plane displaces by twice
    /// its offset; a repeat/wallpaper lattice reaches its per-axis limit times its spacing (an unbounded limit
    /// yields a bound far past any camera, disabling the cull rather than lying to it).</summary>
    /// <param name="domain">The shape's domain ops, or null/empty for 0.</param>
    /// <returns>The displacement bound, creation units.</returns>
    public static float Reach(IReadOnlyList<ShapeDomainOp>? domain) {
        if (domain is not { Count: > 0 } ops) {
            return 0f;
        }

        // 2/√3: the axial-diagonal stretch of the hex lattice's cell centers relative to its pitch.
        const float HexDiagonal = 1.1547005f;

        var reach = 0f;

        foreach (var op in ops) {
            reach += op switch {
                ShapeDomainOp.Symmetry symmetry => (2f * MathF.Abs(x: (symmetry.Offset ?? 0f))),
                ShapeDomainOp.Repeat repeat => (repeat.Spacing.Value * (repeat.Limit?.Value ?? new Vector3(value: ShapeDomainOp.Repeat.UnboundedLimit))).Length(),
                ShapeDomainOp.Polar => 0f,
                ShapeDomainOp.Wallpaper wallpaper => WallpaperReach(wallpaper: wallpaper),
                _ => 0f,
            };
        }

        return reach;

        static float WallpaperReach(ShapeDomainOp.Wallpaper wallpaper) {
            var cell = wallpaper.Cell.Value;
            var limit = (wallpaper.Limit?.Value ?? new Vector2(value: ShapeDomainOp.Wallpaper.UnboundedLimit));

            return ((wallpaper.Group >= SdfWallpaperGroup.P3)
                ? ((cell.X * (limit.X + limit.Y)) * HexDiagonal)
                : new Vector2(
                    x: (cell.X * limit.X),
                    y: (cell.Y * limit.Y)
                ).Length()
            );
        }
    }
    /// <summary>Converts a shape's authored ops into the SDF VM vocabulary, resolving every absent optional to its
    /// documented default.</summary>
    /// <param name="domain">The shape's domain ops, or null/empty.</param>
    /// <returns>The converted ops, in authored order; empty when there are none.</returns>
    public static IReadOnlyList<SdfDomainOp> ToDomainOps(IReadOnlyList<ShapeDomainOp>? domain) {
        if (domain is not { Count: > 0 } ops) {
            return [];
        }

        var mapped = new SdfDomainOp[ops.Count];

        for (var index = 0; (index < ops.Count); index++) {
            mapped[index] = Map(op: ops[index]);
        }

        return mapped;
    }
    /// <summary>Returns whether a shape's authored ops expand to a finite copy set, and that set.</summary>
    /// <param name="domain">The shape's domain ops, or null/empty for the identity copy.</param>
    /// <param name="frames">The copies; a single identity frame when there are no ops, and empty on refusal.</param>
    /// <param name="refusal">Empty on success; otherwise a noun phrase naming what could not expand.</param>
    /// <returns><see langword="true"/> when the chain expanded.</returns>
    public static bool TryExpand(IReadOnlyList<ShapeDomainOp>? domain, out SdfRigidFrame[] frames, out string refusal) =>
        SdfDomainExpansion.TryExpand(
            domain: ToDomainOps(domain: domain),
            frames: out frames,
            refusal: out refusal
        );
}
