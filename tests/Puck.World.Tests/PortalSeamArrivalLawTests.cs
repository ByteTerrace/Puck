using Xunit;

using Puck.Maths;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the seam-fed half of a MAPPED ARRIVAL crossing — a traveler must leave from the exact point
/// its swept segment crossed a portal face (<see cref="WorldFaceCrossing.SeamU"/>/<see cref="WorldFaceCrossing.SeamV"/>,
/// converted back to world space by <see cref="WorldFaceFrame.PointAt"/>) and land at the CORRESPONDING point on the
/// counterpart face — never at the two faces' own placement origins, which <c>Puck.World.WorldInstanceHost</c> used to
/// hand <see cref="WorldPortalArrivalMath.ComputeArrival"/> in place of the seam. <c>WorldInstanceHost</c> (the
/// composition root) is out of reach for this project (see <c>PortalSweepOriginLawTests</c>' own remarks for the same
/// "prove the primitive, not the orchestration" shape) — the seam-fed scan/coalesce/transfer wiring itself is verified
/// by RUNNING <c>Puck.World</c> across the play/studio mapped border (CLAUDE.md rule 3).
/// </summary>
public sealed class PortalSeamArrivalLawTests {
    private static readonly FixedVector3 s_upAxis = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedQ4816 s_flipRadians = FixedQ4816.FromDouble(value: Math.PI);

    private static FixedQ4816 DegreesToRadians(double degrees) => FixedQ4816.FromDouble(value: (degrees * (Math.PI / 180.0)));

    // Builds a yaw-only face frame the SAME way WorldFaceCatalog.DeriveFrame does for an unrotated shape: Right/Up/
    // Normal are the placement's own axis-aligned triad rotated by yawDegrees about world up.
    private static WorldFaceFrame BuildFrame(FixedVector3 origin, double yawDegrees) {
        var rotation = FixedQuaternion.FromAxisAngle(axis: s_upAxis, angle: DegreesToRadians(degrees: yawDegrees));

        return new WorldFaceFrame(
            Origin: origin,
            Right: rotation.Rotate(vector: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)).Normalize(),
            Up: s_upAxis,
            Normal: rotation.Rotate(vector: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One)).Normalize(),
            HalfWidth: FixedQ4816.FromDouble(value: 3.0),
            HalfHeight: FixedQ4816.FromDouble(value: 4.0),
            HalfDepth: FixedQ4816.FromDouble(value: 0.15)
        );
    }

    [Fact]
    public void ComputeArrival_SeamAnchoredBothSides_LandsAnOffCenterCrossingAtItsCounterpartSeam() {
        // An OFF-CENTER crossing (u,v != 0,0) is the discriminating case: a traveler who crosses dead-center (u=v=0)
        // cannot tell a seam-anchored isometry from an origin-anchored one — PointAt(0,0) is Origin. A door is rarely
        // walked through dead-center, and never guaranteed to be.
        var sourceFrame = BuildFrame(origin: new FixedVector3(X: FixedQ4816.FromInteger(value: -10), Y: FixedQ4816.FromDouble(value: 1.5), Z: FixedQ4816.FromInteger(value: 6)), yawDegrees: 90.0);
        var destinationFrame = BuildFrame(origin: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: 1.5), Z: FixedQ4816.FromInteger(value: -8)), yawDegrees: 0.0);

        var seamU = FixedQ4816.FromDouble(value: 1.25);
        var seamV = FixedQ4816.FromDouble(value: 0.4);
        var sourceSeam = sourceFrame.PointAt(u: seamU, v: seamV);
        // The mapped image, not a fresh sample: the destination point applies (-u, v), including the isometry's
        // horizontal flip, to the counterpart's own frame — never re-derived from where the
        // traveler happens to land, which does not exist until this isometry produces it.
        var destinationSeam = WorldPortalArrivalMath.CounterpartSeam(destinationFrame: destinationFrame, seamU: seamU, seamV: seamV);

        // A REALISTIC drain-time capture: the traveler is not frozen exactly at the crossing instant — it walked a
        // little further along the source frame's own Normal between the scan that found the crossing and the drain
        // that applies it. A zero residual would make this indistinguishable from a hand-placed exact point; this
        // keeps the law honest about a nonzero, off-seam traveler position.
        var residualDepth = FixedQ4816.FromDouble(value: 0.3);
        var travelerPosition = (sourceSeam + (sourceFrame.Normal * residualDepth));
        var travelerYaw = DegreesToRadians(degrees: 15.0);
        var travelerPlanarVelocity = new FixedVector3(X: FixedQ4816.FromDouble(value: 2.0), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: -1.0));
        var travelerVerticalVelocity = FixedQ4816.Zero;

        var mapped = WorldPortalArrivalMath.ComputeArrival(
            travelerPosition: travelerPosition,
            travelerYawRadians: travelerYaw,
            travelerPlanarVelocity: travelerPlanarVelocity,
            travelerVerticalVelocity: travelerVerticalVelocity,
            sourcePosition: sourceSeam,
            sourceYawRadians: sourceFrame.PlanarYawRadians,
            destinationPosition: destinationSeam,
            destinationYawRadians: destinationFrame.PlanarYawRadians
        );

        // Independently reconstructed: the residual (the ONLY part of the traveler's offset from the seam once the
        // seam itself is the anchor) rotated by the SAME deltaYaw ComputeArrival derives, landing relative to the
        // DESTINATION's own seam — bit-exact, not approximate: (sourceSeam + residual) - sourceSeam collapses to
        // residual exactly (fixed-point addition/subtraction is exact), so this reconstruction and ComputeArrival's
        // own internal offset rotate the identical input vector through the identical rotation.
        var deltaYaw = ((destinationFrame.PlanarYawRadians - sourceFrame.PlanarYawRadians) + s_flipRadians);
        var rotation = FixedQuaternion.FromAxisAngle(axis: s_upAxis, angle: deltaYaw);
        var expectedPosition = (destinationSeam + rotation.Rotate(vector: (sourceFrame.Normal * residualDepth)));

        Assert.Equal(expected: expectedPosition, actual: mapped.Position);

        // THE DEFECT THIS LAW REPLACES: feeding the two faces' own placement ORIGINS (what WorldInstanceHost used to
        // hand ComputeArrival before the seam threaded through) does NOT land the traveler at the destination's
        // corresponding seam — it is off by twice the off-center offset, mirrored across the destination's own Right
        // axis. This is not asserting a defect in ComputeArrival itself (the isometry has always been correct for
        // whatever points it is given); it pins WHY the origin-anchored call was the wrong caller, so a regression
        // back to origin-anchoring is caught here rather than only by walking the game.
        var originAnchored = WorldPortalArrivalMath.ComputeArrival(
            travelerPosition: travelerPosition,
            travelerYawRadians: travelerYaw,
            travelerPlanarVelocity: travelerPlanarVelocity,
            travelerVerticalVelocity: travelerVerticalVelocity,
            sourcePosition: sourceFrame.Origin,
            sourceYawRadians: sourceFrame.PlanarYawRadians,
            destinationPosition: destinationFrame.Origin,
            destinationYawRadians: destinationFrame.PlanarYawRadians
        );

        Assert.NotEqual(expected: expectedPosition, actual: originAnchored.Position);
    }

    [Fact]
    public void PointAt_CenterOfFace_IsExactlyOrigin() {
        var frame = BuildFrame(origin: new FixedVector3(X: FixedQ4816.FromInteger(value: 4), Y: FixedQ4816.FromInteger(value: 2), Z: FixedQ4816.FromInteger(value: -3)), yawDegrees: 47.0);

        Assert.Equal(expected: frame.Origin, actual: frame.PointAt(u: FixedQ4816.Zero, v: FixedQ4816.Zero));
    }
}
