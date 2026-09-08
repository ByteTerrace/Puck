using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>
/// One domain operator applied to a shape's evaluation point before the shape's own translate/rotate/scale — the
/// isometric subset of the ISA's point ops, named as data so a caller can carry a fold without carrying a builder.
/// </summary>
/// <remarks>Every member of this family is an isometry, which is what lets <see cref="SdfDomainExpansion"/> answer the
/// same fold as a finite set of rigid copies. Non-isometric point ops (twist, bend, log-sphere, displace, domain warp,
/// cell jitter) are deliberately absent: they have no rigid-copy spelling, so they stay builder-only.</remarks>
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
    public sealed record Repeat(Vector3 Spacing, Vector3 Limit) : SdfDomainOp;
    /// <summary>Angular domain repeat — <see cref="SdfProgramBuilder.RepeatPolar"/>.</summary>
    /// <param name="Count">The sector count around the axis.</param>
    /// <param name="Axis">The rotation axis; the fold acts in the plane perpendicular to it.</param>
    /// <param name="Mirror">Whether adjacent sectors mirror across their shared bisector.</param>
    /// <param name="MaterialStride">The per-sector palette stride (0 = geometric only).</param>
    public sealed record Polar(int Count, SdfPolarAxis Axis = SdfPolarAxis.Y, bool Mirror = false, int MaterialStride = 0) : SdfDomainOp;
    /// <summary>Wallpaper-group lattice fold — <see cref="SdfProgramBuilder.WallpaperFold"/>.</summary>
    /// <param name="Group">The wallpaper group.</param>
    /// <param name="Cell">The lattice cell extents in the fold plane.</param>
    /// <param name="Limit">The repeat-cell limit per plane axis.</param>
    /// <param name="Plane">The fold plane.</param>
    /// <param name="MaterialStride">The parity-material stride (0 = geometric only).</param>
    /// <param name="LodDistance">The symmetry-LOD distance threshold (0 = off).</param>
    public sealed record Wallpaper(
        SdfWallpaperGroup Group,
        Vector2 Cell,
        Vector2 Limit,
        SdfWallpaperPlane Plane = SdfWallpaperPlane.XZ,
        int MaterialStride = 0,
        float LodDistance = 0f
    ) : SdfDomainOp;
}
/// <summary>Applies an ordered <see cref="SdfDomainOp"/> list to a builder chain — the one place the family's
/// argument mapping onto <see cref="SdfProgramBuilder"/> is written.</summary>
public static class SdfDomainOps {
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

    private static SdfProgramBuilder ApplyOne(SdfProgramBuilder chain, SdfDomainOp op) {
        return op switch {
            SdfDomainOp.Symmetry symmetry => chain.SymmetryPlane(
                normal: symmetry.Normal,
                offset: symmetry.Offset
            ),
            SdfDomainOp.Repeat repeat => chain.RepeatLimited(
                limit: repeat.Limit,
                spacing: repeat.Spacing
            ),
            SdfDomainOp.Polar polar => chain.RepeatPolar(
                axis: polar.Axis,
                count: polar.Count,
                materialStride: polar.MaterialStride,
                mirror: polar.Mirror
            ),
            SdfDomainOp.Wallpaper wallpaper => chain.WallpaperFold(
                cell: wallpaper.Cell,
                group: wallpaper.Group,
                limit: wallpaper.Limit,
                lodDistance: wallpaper.LodDistance,
                materialStride: wallpaper.MaterialStride,
                plane: wallpaper.Plane
            ),
            _ => throw new ArgumentOutOfRangeException(
                paramName: nameof(op),
                actualValue: op,
                message: "The domain op kind is not defined."
            ),
        };
    }
}
