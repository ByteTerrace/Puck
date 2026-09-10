using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The authored form of a <see cref="SdfProgramBuilder.Sweep"/> — a quadratic Bezier curve swept with a
/// tapering, optionally bulging radius, optionally as helical strands orbiting the curve (the study's hair locks and
/// braid). Admitted only on <see cref="ShapeDocument.Type"/> <see cref="SdfSolidPrimitive.Sweep"/> (refused
/// elsewhere, by name), and required there (a Sweep with no curve is refused, by name). Not a closed solid: refused
/// alongside <see cref="ShapeDocument.Panel"/>, <see cref="ShapeDocument.Trims"/>, <see cref="ShapeDocument.Flare"/>,
/// <see cref="ShapeDocument.Shear"/>, <see cref="ShapeDocument.Bumps"/>, and <see cref="ShapeDocument.Domain"/> on
/// the same shape, and contributes no contact/collider geometry — <see cref="ShapeDocument.Scale"/> must be uniform
/// (refused otherwise, by name) and multiplies every one of the curve's own lengths, exactly as it does for every
/// other primitive's canonical unit dimensions.</summary>
/// <param name="A">The curve's first control point, in the shape's local (workbench) frame — creation units, or a
/// <c>state.&lt;row&gt;[.&lt;key&gt;]</c> reference.</param>
/// <param name="B">The curve's middle control point.</param>
/// <param name="C">The curve's last control point.</param>
/// <param name="RadiusStart">The sweep radius at <c>t = 0</c>; finite and strictly positive.</param>
/// <param name="RadiusEnd">The sweep radius at <c>t = 1</c>; finite and strictly positive.</param>
/// <param name="Bulge">The mid-span radius bulge amplitude (null = 0), added by <c>bulge·sin(π·t)^0.65</c>; refused
/// past <see cref="SdfProgramBuilder.MaxSweepBulgeRatio"/> times <c>max(RadiusStart, RadiusEnd)</c>.</param>
/// <param name="Strands">The helical strand count (null = 1), in [<see cref="SdfProgramBuilder.MinSweepStrands"/>,
/// <see cref="SdfProgramBuilder.MaxSweepStrands"/>]. A count above 1 is render-only — refused for deterministic
/// field contact by name, matching <see cref="SdfShapeType.Sweep"/>'s own status.</param>
/// <param name="Twist">The strand orbit rate, in turns along the curve (null = 0); finite.</param>
/// <param name="StrandOffset">The strand orbit radius, in creation units (null = 0); non-negative, finite, refused
/// past <see cref="SdfProgramBuilder.MaxSweepStrandOffsetRatio"/> times <c>max(RadiusStart, RadiusEnd)</c>.</param>
public sealed record ShapeCurveDocument(
    DocumentVector3 A,
    DocumentVector3 B,
    DocumentVector3 C,
    float RadiusStart,
    float RadiusEnd,
    float? Bulge = null,
    int? Strands = null,
    float? Twist = null,
    float? StrandOffset = null
) {
    /// <summary>Returns this curve's resolved, unscaled parameters for
    /// <see cref="SdfSolidGeometry.AppendScaledPrimitive"/>.</summary>
    public SdfSweepParameters Parameters() => new(
        A: A,
        B: B,
        Bulge: (Bulge ?? 0f),
        C: C,
        RadiusEnd: RadiusEnd,
        RadiusStart: RadiusStart,
        Strands: (Strands ?? SdfProgramBuilder.MinSweepStrands),
        StrandOffset: (StrandOffset ?? 0f),
        Twist: (Twist ?? 0f)
    );
    /// <summary>Returns this curve's worst-case reach from the shape's local origin, at unit scale — see
    /// <see cref="SdfSolidGeometry.SweepReach"/>.</summary>
    public float Reach() => SdfSolidGeometry.SweepReach(
        a: A,
        b: B,
        bulge: (Bulge ?? 0f),
        c: C,
        radiusEnd: RadiusEnd,
        radiusStart: RadiusStart,
        strandOffset: (StrandOffset ?? 0f)
    );
}
