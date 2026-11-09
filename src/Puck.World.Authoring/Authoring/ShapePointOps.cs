using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The ordered local point deformations shared by static and pooled creation emission.</summary>
public static class ShapePointOps {
    [Flags]
    private enum Operations {
        None = 0, Twist = 1, Bend = 2, Flare = 4, Shear = 8, Bumps = 16, Warp = 32, Elongate = 64,
    }

    // The emission mask is also the scope mask: every point operation needs isolation unless it is
    // explicitly known to be 1-Lipschitz (elongation). Adding an emitted operation cannot omit its scope.
    private static Operations Active(float twist, float bend, ShapeFlareDocument? flare,
        ShapeShearDocument? shear, IReadOnlyList<ShapeBumpDocument>? bumps, ShapeWarpDocument? warp,
        DocumentVector3? elongate, bool probeWorstCase = false) =>
        (probeWorstCase ? (Operations)(-1) :
            ((twist != 0f) ? Operations.Twist : Operations.None) |
            ((bend != 0f) ? Operations.Bend : Operations.None) |
            ((flare is not null) ? Operations.Flare : Operations.None) |
            ((shear is not null) ? Operations.Shear : Operations.None) |
            ((bumps is { Count: > 0 }) ? Operations.Bumps : Operations.None) |
            ((warp is not null) ? Operations.Warp : Operations.None) |
            ((elongate is not null) ? Operations.Elongate : Operations.None));

    /// <summary>Whether a shape's point deformations or relief need an isolated Lipschitz clamp.</summary>
    public static bool NeedsScope(ShapeDocument shape) => NeedsScope((shape.Twist ?? 0f), (shape.Bend ?? 0f),
        shape.Flare, shape.Shear, shape.Bumps, shape.Warp, shape.Erode, shape.Cells, shape.Displace);
    /// <summary>The scope decision shared by static emission, pooled emission and bake partitioning.</summary>
    public static bool NeedsScope(float twist = 0f, float bend = 0f, ShapeFlareDocument? flare = null,
        ShapeShearDocument? shear = null, IReadOnlyList<ShapeBumpDocument>? bumps = null,
        ShapeWarpDocument? warp = null, ShapeErodeDocument? erode = null, ShapeCellsDocument? cells = null,
        ShapeDisplaceDocument? displace = null) =>
        (((Active(twist, bend, flare, shear, bumps, warp, null) & ~Operations.Elongate) != Operations.None) ||
        (erode is not null) || (cells is not null) || (displace is not null));
    /// <summary>Whether contact must evaluate the shape's point deformation rather than its straight primitive.</summary>
    public static bool DeformsContact(ShapeDocument shape) =>
        (Active((shape.Twist ?? 0f), (shape.Bend ?? 0f), null, null, null, null, null) != Operations.None);
    /// <summary>Applies twist, bend, flare, shear, bumps, warp and elongation in document order.
    /// <paramref name="lengthScale"/> converts authored creation lengths into the chain's point units;
    /// inverse lengths convert reciprocally. A chain already carrying the placement Scale uses one.</summary>
    public static SdfProgramBuilder Apply(SdfProgramBuilder chain, float lengthScale = 1f, float twist = 0f,
        float bend = 0f, ShapeFlareDocument? flare = null, ShapeShearDocument? shear = null,
        IReadOnlyList<ShapeBumpDocument>? bumps = null, ShapeWarpDocument? warp = null,
        DocumentVector3? elongate = null, bool probeWorstCase = false) {
        var operations = Active(bend: bend, bumps: bumps, elongate: elongate, flare: flare, probeWorstCase: probeWorstCase, shear: shear, twist: twist, warp: warp);

        if ((operations & Operations.Twist) != 0) {
            chain = chain.TwistY(rate: (probeWorstCase
                ? 1f
                : (twist / lengthScale)));
        }

        if ((operations & Operations.Bend) != 0) {
            chain = chain.BendY(rate: (probeWorstCase
                ? 1f
                : (bend / lengthScale)));
        }

        if ((operations & Operations.Flare) != 0) {
            chain = chain.AxialProfile(
                amount: (probeWorstCase ? ShapeFlareDocument.MaxAmount : flare!.Amount),
                bulge: (probeWorstCase ? ShapeFlareDocument.MaxBulge : flare!.Bulge),
                top: (probeWorstCase ? 0f : ((flare!.Top ?? 0f) * lengthScale)),
                span: (probeWorstCase ? 1f : (flare!.Span * lengthScale)),
                axis: (probeWorstCase ? SdfAxis.Y : (SdfAxis)flare!.Axis),
                startScale: (probeWorstCase ? ShapeFlareDocument.MaxStartScale : flare!.StartScale)
            );
        }

        if ((operations & Operations.Shear) != 0) {
            chain = chain.Shear(
                linear: (probeWorstCase ? ShapeDocument.MaxShear : shear!.Linear),
                quadratic: (probeWorstCase ? ShapeDocument.MaxShear : (shear!.Quadratic / lengthScale)),
                cubic: (probeWorstCase ? ShapeDocument.MaxShear : (shear!.Cubic / (lengthScale * lengthScale))),
                target: (probeWorstCase ? SdfAxis.X : (SdfAxis)shear!.Target),
                driver: (probeWorstCase ? SdfAxis.Y : (SdfAxis)shear!.Driver)
            );
        }

        if (probeWorstCase) {
            // Worst-case reserves the widest declared bump list; every entry is otherwise identical for capacity
            // purposes (each costs the same single instruction) — Push length feeds the caller's reach probe,
            // not the instruction count.
            for (var i = 0; (i < ShapeBumpDocument.MaxBumps); i++) {
                chain = chain.GaussianPush(center: Vector3.Zero, radii: Vector3.One, push: new Vector3(value: ShapeBumpDocument.MaxPushMagnitude));
            }
        } else if ((operations & Operations.Bumps) != 0) {
            foreach (var bump in (bumps ?? [])) {
                chain = chain.GaussianPush(
                    center: (bump.Center.Value * lengthScale),
                    radii: (bump.Radii.Value * lengthScale),
                    push: (bump.Push.Value * lengthScale)
                );
            }
        }

        if ((operations & Operations.Warp) != 0) {
            chain = chain.DomainWarp(
                amplitude: (probeWorstCase ? ShapeWarpDocument.MaxAmplitude : (warp!.Amplitude * lengthScale)),
                frequency: (probeWorstCase ? new Vector3(value: ShapeWarpDocument.MaxFrequency) : (warp!.Frequency.Value / lengthScale))
            );
        }

        if ((operations & Operations.Elongate) != 0) {
            chain = chain.Elongate(extents: (probeWorstCase ? new Vector3(value: ShapeDocument.MaxElongate) : (elongate!.Value * lengthScale)));
        }

        return chain;
    }
}
