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
        DocumentVector3? elongate) =>
        ((twist != 0f) ? Operations.Twist : Operations.None) |
        ((bend != 0f) ? Operations.Bend : Operations.None) |
        ((flare is not null) ? Operations.Flare : Operations.None) |
        ((shear is not null) ? Operations.Shear : Operations.None) |
        ((bumps is { Count: > 0 }) ? Operations.Bumps : Operations.None) |
        ((warp is not null) ? Operations.Warp : Operations.None) |
        ((elongate is not null) ? Operations.Elongate : Operations.None);

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
    /// <summary>Applies twist, bend, flare, shear, bumps, warp and elongation in document order, in the chain's point
    /// units, which are the creation units the shape is authored in.</summary>
    /// <param name="chain">The chain the deformations apply to.</param>
    /// <param name="twist">The twist rate, radians per creation unit of local Y.</param>
    /// <param name="bend">The bend rate, radians per creation unit of local Y.</param>
    /// <param name="flare">The flare, or <see langword="null"/> for none.</param>
    /// <param name="shear">The shear, or <see langword="null"/> for none.</param>
    /// <param name="bumps">The gaussian pushes, or <see langword="null"/> for none.</param>
    /// <param name="warp">The sine warp, or <see langword="null"/> for none.</param>
    /// <param name="elongate">The elongation half-extents, or <see langword="null"/> for none.</param>
    /// <returns>The chain.</returns>
    public static SdfProgramBuilder Apply(SdfProgramBuilder chain, float twist = 0f,
        float bend = 0f, ShapeFlareDocument? flare = null, ShapeShearDocument? shear = null,
        IReadOnlyList<ShapeBumpDocument>? bumps = null, ShapeWarpDocument? warp = null,
        DocumentVector3? elongate = null) {
        var operations = Active(bend: bend, bumps: bumps, elongate: elongate, flare: flare, shear: shear, twist: twist, warp: warp);

        if ((operations & Operations.Twist) != 0) {
            chain = chain.TwistY(rate: twist);
        }

        if ((operations & Operations.Bend) != 0) {
            chain = chain.BendY(rate: bend);
        }

        if ((operations & Operations.Flare) != 0) {
            chain = chain.AxialProfile(
                amount: flare!.Amount,
                bulge: flare.Bulge,
                top: (flare.Top ?? 0f),
                span: flare.Span,
                axis: (SdfAxis)flare.Axis,
                startScale: flare.StartScale
            );
        }

        if ((operations & Operations.Shear) != 0) {
            chain = chain.Shear(
                linear: shear!.Linear,
                quadratic: shear.Quadratic,
                cubic: shear.Cubic,
                target: (SdfAxis)shear.Target,
                driver: (SdfAxis)shear.Driver
            );
        }

        if ((operations & Operations.Bumps) != 0) {
            foreach (var bump in bumps!) {
                chain = chain.GaussianPush(
                    center: bump.Center.Value,
                    radii: bump.Radii.Value,
                    push: bump.Push.Value
                );
            }
        }

        if ((operations & Operations.Warp) != 0) {
            chain = chain.DomainWarp(
                amplitude: warp!.Amplitude,
                frequency: warp.Frequency.Value
            );
        }

        if ((operations & Operations.Elongate) != 0) {
            chain = chain.Elongate(extents: elongate!.Value);
        }

        return chain;
    }
}
