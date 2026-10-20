using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>
/// One domain operator applied to a shape's evaluation point before the shape's own translate/rotate/scale — the
/// isometric subset of the ISA's point ops, named as data so a caller can carry a fold without carrying a builder.
/// </summary>
/// <remarks>The reflections, repeats and polar folds are isometries, which is what lets <see cref="SdfDomainExpansion"/>
/// answer the same fold as a finite set of rigid copies. The wallpaper, jitter and log-sphere folds have no finite
/// rigid-copy spelling (a lattice without an edge, a hashed copy per cell, a similarity per shell), so the expansion
/// refuses each by name and a solid placement carrying one is refused at validation. The non-isometric point warps
/// (twist, bend, displace, domain warp) are per-shape facets, not folds.</remarks>
public abstract record SdfDomainOp {
    private SdfDomainOp() {
    }

    /// <summary>Reflection fold across a plane — <see cref="SdfProgramBuilder.SymmetryPlane"/>.</summary>
    /// <param name="Normal">The plane normal (normalized on use; a zero/non-finite normal is refused).</param>
    /// <param name="Offset">The plane's signed offset along <paramref name="Normal"/>.</param>
    public sealed record Symmetry(Vector3 Normal, float Offset = 0f) : SdfDomainOp;
    /// <summary>Bounded linear domain repeat — <see cref="SdfProgramBuilder.RepeatLimited"/>.</summary>
    /// <param name="Spacing">The per-axis cell spacing.</param>
    /// <param name="Limit">The per-axis repeat-cell limit; the lattice spans cell indices -limit..+limit.</param>
    /// <param name="Origin">The point the lattice folds around, in the frame this op evaluates in (zero = the
    /// creation origin, the pre-existing fold centre). A nonzero origin sandwiches the fold between a translate to
    /// and from the origin, so cell selection is centred on it instead of the creation root — the lattice of
    /// physical copies itself (their spacing, count) is unchanged; only which point resolves to which copy moves.
    /// Zero is byte-identical to the origin-free fold.</param>
    public sealed record Repeat(Vector3 Spacing, Vector3 Limit, Vector3 Origin = default) : SdfDomainOp;
    /// <summary>Angular domain repeat — <see cref="SdfProgramBuilder.RepeatPolar"/>.</summary>
    /// <param name="Count">The sector count around the axis.</param>
    /// <param name="Axis">The rotation axis; the fold acts in the plane perpendicular to it.</param>
    /// <param name="Mirror">Whether adjacent sectors mirror across their shared bisector.</param>
    /// <param name="MaterialStride">The per-sector palette stride (0 = geometric only).</param>
    /// <param name="Origin">The point the sector fold pivots around, in the frame this op evaluates in (zero = the
    /// creation origin, the pre-existing pivot). A nonzero origin sandwiches the fold between a translate to and
    /// from the origin, so sectors fan out around it instead of the creation root. Zero is byte-identical to the
    /// origin-free fold.</param>
    /// <param name="Subject">The point the folded content is centred on, in the same frame as
    /// <paramref name="Origin"/>: the fold turns so its first sector's bisector passes through it, so content authored
    /// at any angle about the axis lies inside the sector the fold evaluates and draws one copy per sector, as
    /// <see cref="SdfDomainExpansion"/> expands it; a mirrored fold reflects across the plane through the axis and
    /// it. A subject on the axis (zero, the default) keeps the bisector on the fold plane's first axis.</param>
    public sealed record Polar(int Count, SdfAxis Axis = SdfAxis.Y, bool Mirror = false, int MaterialStride = 0, Vector3 Origin = default, Vector3 Subject = default) : SdfDomainOp {
        /// <summary>Returns the fold plane's two world-axis indices: the first axis the unturned bisector lies along,
        /// then the second, which <c>SDF_OP_REPEAT_POLAR</c> measures angles toward.</summary>
        /// <param name="axis">The fold's rotation axis.</param>
        /// <returns>The plane's (u, v) axis indices.</returns>
        public static (int U, int V) PlaneOf(SdfAxis axis) => axis switch {
            SdfAxis.X => (U: 1, V: 2),
            SdfAxis.Z => (U: 0, V: 1),
            _ => (U: 0, V: 2),
        };
    }
    /// <summary>Wallpaper-group lattice fold — <see cref="SdfProgramBuilder.WallpaperFold"/>.</summary>
    /// <param name="Group">The wallpaper group.</param>
    /// <param name="Cell">The lattice cell extents in the fold plane.</param>
    /// <param name="Limit">The repeat-cell limit per plane axis.</param>
    /// <param name="Plane">The fold plane.</param>
    /// <param name="MaterialStride">The parity-material stride (0 = geometric only).</param>
    public sealed record Wallpaper(
        SdfWallpaperGroup Group,
        Vector2 Cell,
        Vector2 Limit,
        SdfPlane Plane = SdfPlane.XZ,
        int MaterialStride = 0
    ) : SdfDomainOp;
    /// <summary>Stochastic lattice fold — <see cref="SdfProgramBuilder.CellJitter"/>: a repeat whose every cell
    /// displaces, tumbles and recolours its copy by a hash of the cell.</summary>
    /// <param name="Spacing">The per-axis cell spacing.</param>
    /// <param name="Amount">The peak-to-peak hashed displacement per axis; under half the least spacing.</param>
    /// <param name="Seed">The hash seed.</param>
    /// <param name="Tumble">The hashed rotation's share of a half turn, in [0, 1]; zero keeps every copy upright.</param>
    /// <param name="MaterialVariants">The hashed palette rows a copy's material strides over (0 = geometric only).</param>
    /// <param name="Flavor">How the displacement is distributed over the cell.</param>
    /// <param name="Origin">The point the lattice folds around, as <see cref="Repeat.Origin"/>.</param>
    public sealed record Jitter(Vector3 Spacing, float Amount, uint Seed = 0u, float Tumble = 0f, int MaterialVariants = 0, SdfNoiseFlavor Flavor = SdfNoiseFlavor.White, Vector3 Origin = default) : SdfDomainOp;
    /// <summary>Log-spherical shell fold — <see cref="SdfProgramBuilder.LogSphere"/>: space tiled into self-similar
    /// shells about the origin, each a scaled copy of the prototype shell.</summary>
    /// <param name="ShellRatio">The ratio between neighbouring shells' radii, above 1.</param>
    /// <param name="Twist">The spin each shell turns about the Z axis, in radians per shell.</param>
    /// <param name="Origin">The point the shells centre on, as <see cref="Repeat.Origin"/>.</param>
    public sealed record LogSphere(float ShellRatio, float Twist = 0f, Vector3 Origin = default) : SdfDomainOp;
}
/// <summary>Applies an ordered <see cref="SdfDomainOp"/> list to a builder chain — the one place the family's
/// argument mapping onto <see cref="SdfProgramBuilder"/> is written.</summary>
public static class SdfDomainOps {
    /// <summary>The per-axis repeat-cell limit that leaves a lattice without an edge: far past any authored reach, so no
    /// cell index is ever clamped.</summary>
    public const float UnboundedRepeatLimit = 1000000f;

    /// <summary>Whether <paramref name="limit"/> leaves a repeat lattice without an edge: any axis at
    /// <see cref="UnboundedRepeatLimit"/>. Such a repeat has copies at every distance on that axis, so its influence has
    /// no bound, and every reader of the limit takes this one answer for it rather than the sentinel as a number of
    /// cells.</summary>
    /// <param name="limit">The per-axis repeat-cell limit.</param>
    /// <returns><see langword="true"/> when the repeat's influence is unbounded.</returns>
    public static bool IsUnboundedRepeat(System.Numerics.Vector3 limit) => ((limit.X >= UnboundedRepeatLimit) || (limit.Y >= UnboundedRepeatLimit) || (limit.Z >= UnboundedRepeatLimit));

    // SDF_OP_REPEAT_POLAR keeps the sector whose bisector lies along the fold plane's first axis. A subject off the
    // axis turns that bisector onto the subject: the point turns back by the subject's angle before the fold and forward
    // after it. The angle is measured from the plane's first axis toward its second, as the shader's atan2 measures it;
    // about Y that direction is a negative right-handed turn (the (x, z) pair is left-handed about +Y).
    private static SdfProgramBuilder ApplyPolar(SdfProgramBuilder chain, SdfDomainOp.Polar polar) {
        var (u, v) = SdfDomainOp.Polar.PlaneOf(axis: polar.Axis);
        var relative = (polar.Subject - polar.Origin);
        var planeU = Component(vector: relative, index: u);
        var planeV = Component(vector: relative, index: v);
        var angle = (((planeU != 0f) || (planeV != 0f))
            ? (MathF.Atan2(y: planeV, x: planeU) * ((polar.Axis is SdfAxis.X or SdfAxis.Z) ? 1f : -1f))
            : 0f);
        var turned = (angle != 0f);
        var axis = polar.Axis switch {
            SdfAxis.X => Vector3.UnitX,
            SdfAxis.Z => Vector3.UnitZ,
            _ => Vector3.UnitY,
        };

        chain = chain.Translate(offset: polar.Origin);
        if (turned) {
            chain = chain.Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: axis, angle: angle));
        }
        chain = chain.RepeatPolar(
            axis: polar.Axis,
            count: polar.Count,
            materialStride: polar.MaterialStride,
            mirror: polar.Mirror
        );
        if (turned) {
            chain = chain.Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: axis, angle: -angle));
        }

        return chain.Translate(offset: -polar.Origin);

        static float Component(Vector3 vector, int index) => index switch {
            0 => vector.X,
            1 => vector.Y,
            _ => vector.Z,
        };
    }
    private static SdfProgramBuilder ApplyOne(SdfProgramBuilder chain, SdfDomainOp op) {
        return op switch {
            SdfDomainOp.Symmetry symmetry => chain.SymmetryPlane(
            normal: symmetry.Normal,
            offset: symmetry.Offset
        ),
            SdfDomainOp.Repeat repeat => chain.Translate(offset: repeat.Origin)
                .RepeatLimited(
            limit: repeat.Limit,
            spacing: repeat.Spacing
        )
                .Translate(offset: -repeat.Origin),
            SdfDomainOp.Polar polar => ApplyPolar(chain: chain, polar: polar),
            SdfDomainOp.Wallpaper wallpaper => chain.WallpaperFold(
            cell: wallpaper.Cell,
            group: wallpaper.Group,
            limit: wallpaper.Limit,
            materialStride: wallpaper.MaterialStride,
            plane: wallpaper.Plane
        ),
            SdfDomainOp.Jitter jitter => chain.Translate(offset: jitter.Origin)
                .CellJitter(
            flavor: jitter.Flavor,
            jitter: jitter.Amount,
            materialVariants: jitter.MaterialVariants,
            seed: jitter.Seed,
            spacing: jitter.Spacing,
            tumble: jitter.Tumble
        )
                .Translate(offset: -jitter.Origin),
            SdfDomainOp.LogSphere logSphere => chain.Translate(offset: logSphere.Origin)
                .LogSphere(
            shellRatio: logSphere.ShellRatio,
            twist: logSphere.Twist
        )
                .Translate(offset: -logSphere.Origin),
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(op),
            actualValue: op,
            message: "The domain op kind is not defined."
        ),
        };
    }

    /// <summary>Applies every op in <paramref name="domain"/>, in order.</summary>
    /// <param name="chain">The builder chain, already advanced past whatever frame precedes the fold.</param>
    /// <param name="domain">The ordered ops, or null/empty for no-op.</param>
    /// <returns><paramref name="chain"/>, for further chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chain"/> is <see langword="null"/>.</exception>
    public static SdfProgramBuilder Apply(SdfProgramBuilder chain, IReadOnlyList<SdfDomainOp>? domain) {
        ArgumentNullException.ThrowIfNull(chain);

        if (domain is not { Count: > 0 } ops) {
            return chain;
        }

        foreach (var op in ops) {
            chain = ApplyOne(
                chain: chain,
                op: op
            );
        }

        return chain;
    }
}
