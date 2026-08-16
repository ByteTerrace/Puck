using Puck.Maths;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldFrameIsometry"/> — the one mapped-arrival isometry portal furniture and
/// invisible adjacency borders both cross by. Pure and fixed-point, so this proves the math directly rather than
/// through <c>Puck.World.WorldInstanceHost</c> (the composition root, out of reach for this project — see
/// <c>PortalSweepOriginLawTests</c>' own remarks for the same "prove the primitive, not the orchestration" shape).
/// The scan/coalesce/transfer/arrival orchestration is verified by RUNNING <c>Puck.World</c> (CLAUDE.md rule 3).
/// </summary>
public sealed class WorldFrameIsometryLawTests {
    private static readonly FixedVector3 s_upAxis = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 s_forward = new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One);
    private static readonly FixedQ4816 s_piRadians = FixedQ4816.FromDouble(value: Math.PI);

    // A crossing point maps through two dot-product decompositions; both round in Q48.16, so a corresponding-point
    // assertion is bounded rather than bit-exact. One raw unit is 1/65536 of a world unit.
    private const long SeamCorrespondenceBudgetRaw = 16;
    private const long PositionErrorBudgetRaw = 32;
    private const long VelocityErrorBudgetRaw = 160;
    private const long YawErrorBudgetRaw = 8;

    private static FixedQ4816 DegreesToRadians(double degrees) => FixedQ4816.FromDouble(value: (degrees * (Math.PI / 180.0)));
    private static long RawAbs(FixedQ4816 a, FixedQ4816 b) => Math.Abs(value: (a.Value - b.Value));
    private static long RawAbs(FixedVector3 a, FixedVector3 b) =>
        Math.Max(val1: RawAbs(a: a.X, b: b.X), val2: Math.Max(val1: RawAbs(a: a.Y, b: b.Y), val2: RawAbs(a: a.Z, b: b.Z)));
    // Builds a yaw-only face frame the SAME way WorldFaceCatalog.DeriveFrame does for an unrotated shape: Right/Up/
    // Normal are the placement's own axis-aligned triad rotated by yawDegrees about world up.
    private static WorldFaceFrame BuildFrame(FixedVector3 origin, double yawDegrees) {
        var rotation = FixedQuaternion.FromAxisAngle(axis: s_upAxis, angle: DegreesToRadians(degrees: yawDegrees));

        return new WorldFaceFrame(
            Origin: origin,
            Right: rotation.Rotate(vector: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)).Normalize(),
            Up: s_upAxis,
            Normal: rotation.Rotate(vector: s_forward).Normalize(),
            HalfWidth: FixedQ4816.FromDouble(value: 3.0),
            HalfHeight: FixedQ4816.FromDouble(value: 4.0),
            HalfDepth: FixedQ4816.FromDouble(value: 0.15)
        );
    }

    [InlineData(90.0, 0.0)]
    [InlineData(0.0, 90.0)]
    [Theory]
    public void MapArrival_OffCentreCrossing_LandsAtItsCounterpartPoint(double sourceYawDegrees, double destinationYawDegrees) {
        // An OFF-CENTRE crossing (u,v != 0,0) is the discriminating case: a traveler who crosses dead-centre cannot
        // tell the isometry from one that drops the half turn's horizontal flip, because PointAt(0,0) is Origin.
        // Both parameter rows together cover the property in BOTH directions of one reciprocal pair.
        var sourceFrame = BuildFrame(origin: new FixedVector3(X: FixedQ4816.FromInteger(value: -10), Y: FixedQ4816.FromDouble(value: 1.5), Z: FixedQ4816.FromInteger(value: 6)), yawDegrees: sourceYawDegrees);
        var destinationFrame = BuildFrame(origin: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: 1.5), Z: FixedQ4816.FromInteger(value: -8)), yawDegrees: destinationYawDegrees);

        var seamU = FixedQ4816.FromDouble(value: 1.25);
        var seamV = FixedQ4816.FromDouble(value: 0.4);
        var crossingPoint = sourceFrame.PointAt(u: seamU, v: seamV);

        var mapped = WorldFrameIsometry.MapArrival(
            travelerPosition: crossingPoint,
            travelerYawRadians: DegreesToRadians(degrees: 15.0),
            travelerPlanarVelocity: FixedVector3.Zero,
            travelerVerticalVelocity: FixedQ4816.Zero,
            source: in sourceFrame,
            destination: in destinationFrame);

        // The corresponding point: the counterpart's own (-u, v). The isometry produces it with no seam carried
        // beside the traveler — the half turn's horizontal flip IS the sign change.
        var counterpart = destinationFrame.PointAt(u: -seamU, v: seamV);

        Assert.True(condition: (RawAbs(a: mapped.Position, b: counterpart) <= SeamCorrespondenceBudgetRaw),
            userMessage: $"mapped {mapped.Position} is more than {SeamCorrespondenceBudgetRaw} raw units from the counterpart point {counterpart}");

        // Two defects the assertion above must actually discriminate: a dropped horizontal flip lands at (+u, v),
        // and an origin-anchored map that forgets the crossing offset entirely lands at the counterpart's centre.
        Assert.True(condition: (RawAbs(a: mapped.Position, b: destinationFrame.PointAt(u: seamU, v: seamV)) > SeamCorrespondenceBudgetRaw));
        Assert.True(condition: (RawAbs(a: mapped.Position, b: destinationFrame.Origin) > SeamCorrespondenceBudgetRaw));
    }
    [Fact]
    public void MapArrival_DifferentYawPair_MapsHeadingAndVelocityThroughTheSameIsometry() {
        // Source (30 degrees) and destination (120 degrees) differ and neither is zero: a same-yaw pair would hide
        // both a dropped half turn and a dropped source-frame subtraction.
        var sourceFrame = BuildFrame(origin: FixedVector3.Zero, yawDegrees: 30.0);
        var destinationFrame = BuildFrame(origin: new FixedVector3(X: FixedQ4816.FromInteger(value: 5), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: 5)), yawDegrees: 120.0);

        var travelerYaw = DegreesToRadians(degrees: 15.0);
        var travelerPlanarVelocity = new FixedVector3(X: FixedQ4816.FromInteger(value: 3), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var travelerVerticalVelocity = -FixedQ4816.One;

        var mapped = WorldFrameIsometry.MapArrival(
            travelerPosition: new FixedVector3(X: FixedQ4816.FromInteger(value: 2), Y: FixedQ4816.One, Z: FixedQ4816.Zero),
            travelerYawRadians: travelerYaw,
            travelerPlanarVelocity: travelerPlanarVelocity,
            travelerVerticalVelocity: travelerVerticalVelocity,
            source: in sourceFrame,
            destination: in destinationFrame);

        // HEADING — the mapped yaw must describe the mapped facing, not merely some angle: rotate world forward by
        // the arrival yaw and compare against the source facing pushed through MapVector.
        var sourceFacing = FixedQuaternion.FromAxisAngle(angle: travelerYaw, axis: s_upAxis).Rotate(vector: s_forward);
        var expectedFacing = WorldFrameIsometry.MapVector(destination: in destinationFrame, source: in sourceFrame, value: sourceFacing);
        var actualFacing = FixedQuaternion.FromAxisAngle(axis: s_upAxis, angle: mapped.YawRadians).Rotate(vector: s_forward);

        Assert.True(condition: (RawAbs(a: actualFacing, b: expectedFacing) <= SeamCorrespondenceBudgetRaw),
            userMessage: $"mapped facing {actualFacing} does not match the isometry's own image {expectedFacing}");

        // The unwrapped convention: an arrival adds one delta to the traveler's own accumulator rather than
        // replacing it, so a body that has already turned past a half turn does not lose its turn count.
        Assert.Equal(expected: (travelerYaw + WorldFrameIsometry.YawDelta(destination: in destinationFrame, source: in sourceFrame)), actual: mapped.YawRadians);

        // VELOCITY — the planar/vertical split is a representation detail; the composed 3-vector is what maps.
        var velocity = new FixedVector3(X: travelerPlanarVelocity.X, Y: travelerVerticalVelocity, Z: travelerPlanarVelocity.Z);
        var expectedVelocity = WorldFrameIsometry.MapVector(destination: in destinationFrame, source: in sourceFrame, value: velocity);

        Assert.Equal(expected: expectedVelocity.X, actual: mapped.PlanarVelocity.X);
        Assert.Equal(expected: expectedVelocity.Z, actual: mapped.PlanarVelocity.Z);
        Assert.Equal(expected: expectedVelocity.Y, actual: mapped.VerticalVelocity);
        Assert.Equal(expected: FixedQ4816.Zero, actual: mapped.PlanarVelocity.Y);
    }
    [Fact]
    public void YawDelta_SameYawPair_IsExactlyTheHalfTurn() {
        // Walking out of a face along its outward normal must walk IN through the counterpart. When both frames
        // share one yaw the frame difference cancels and the half turn is all that remains — proving the flip is
        // unconditional rather than something that only fires when the frames disagree.
        var frame = BuildFrame(origin: FixedVector3.Zero, yawDegrees: 45.0);
        var counterpart = BuildFrame(origin: new FixedVector3(X: FixedQ4816.FromInteger(value: 9), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: -3)), yawDegrees: 45.0);

        Assert.True(condition: (RawAbs(a: WorldFrameIsometry.YawDelta(destination: in counterpart, source: in frame), b: s_piRadians) <= YawErrorBudgetRaw));
    }
    [Fact]
    public void YawDelta_OpposedPair_IsTheIdentityControl() {
        // The control for the law above: a pair whose yaws differ by a half turn composes to zero rotation, so a
        // traveler's heading and offset pass through unchanged. Without it, a YawDelta that always answered pi
        // would satisfy the same-yaw law.
        var frame = BuildFrame(origin: FixedVector3.Zero, yawDegrees: 40.0);
        var counterpart = BuildFrame(origin: FixedVector3.Zero, yawDegrees: -140.0);
        var offset = new FixedVector3(X: FixedQ4816.FromInteger(value: 4), Y: FixedQ4816.FromInteger(value: 2), Z: FixedQ4816.FromInteger(value: -3));

        var mapped = WorldFrameIsometry.MapArrival(
            travelerPosition: offset,
            travelerYawRadians: FixedQ4816.Zero,
            travelerPlanarVelocity: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: -FixedQ4816.FromInteger(value: 2)),
            travelerVerticalVelocity: FixedQ4816.Zero,
            source: in frame,
            destination: in counterpart);

        Assert.True(condition: (Math.Abs(value: WorldFrameIsometry.YawDelta(destination: in counterpart, source: in frame).Value) <= YawErrorBudgetRaw));
        Assert.True(condition: (RawAbs(a: mapped.Position, b: offset) <= SeamCorrespondenceBudgetRaw));
    }
    // A reciprocal round trip is not an exact identity: MapVector's dot products and YawDelta's Atan2 each round in
    // Q48.16, and two crossings compose two of them. The contract is an explicit upper bound on that drift, so a
    // change to the isometry or to FixedQ4816's own trig tables that silently widens it is caught here. The budgets
    // carry headroom over the measured maxima; one raw unit is 1/65536.
    [Fact]
    public void MapArrival_ReciprocalRoundTrip_StaysWithinTheMeasuredErrorBudget() {
        var yawPairsDegrees = new (double SourceYaw, double DestinationYaw, string Label)[] {
            (0.0, 0.0, "same-yaw"),
            (30.0, 120.0, "discriminating"),
            (45.0, 225.0, "opposite"),
            (10.0, -170.0, "near-wrap"),
            (179.9, -179.9, "wrap-crossing"),
            (-45.0, 200.0, "negative/over-180"),
            (90.0, 270.0, "quarter/three-quarter"),
            (33.333, 217.777, "non-round"),
            (0.001, -179.999, "tight wrap"),
            (-89.5, 271.3, "wide spread"),
        };

        var travelerConfigs = new (FixedVector3 Offset, FixedVector3 PlanarVelocity, FixedQ4816 VerticalVelocity, string Label)[] {
            (new FixedVector3(X: FixedQ4816.FromDouble(value: 2.4), Y: FixedQ4816.FromDouble(value: 1.8), Z: FixedQ4816.FromDouble(value: -2.9)),
                new FixedVector3(X: FixedQ4816.FromDouble(value: 12.0), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: -9.0)),
                FixedQ4816.FromDouble(value: -4.5), "moderate"),
            (new FixedVector3(X: FixedQ4816.FromDouble(value: 1.4), Y: FixedQ4816.FromDouble(value: 1.9), Z: FixedQ4816.FromDouble(value: 1.9)),
                new FixedVector3(X: FixedQ4816.FromDouble(value: 28.0), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: -19.0)),
                FixedQ4816.FromDouble(value: -9.0), "boundary-bound offset, high velocity"),
            (new FixedVector3(X: FixedQ4816.FromDouble(value: -1.5), Y: FixedQ4816.FromDouble(value: -1.9), Z: FixedQ4816.FromDouble(value: 0.1)),
                new FixedVector3(X: FixedQ4816.FromDouble(value: 0.5), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: 0.3)),
                FixedQ4816.FromDouble(value: 0.1), "small everything"),
        };

        var sourceOrigin = new FixedVector3(X: FixedQ4816.FromDouble(value: 12.0), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: -18.0));
        var destinationOrigin = new FixedVector3(X: FixedQ4816.FromDouble(value: -40.0), Y: FixedQ4816.FromDouble(value: 2.0), Z: FixedQ4816.FromDouble(value: 55.0));
        var travelerYaw = DegreesToRadians(degrees: 15.0);

        foreach (var (offset, planarVelocity, verticalVelocity, configLabel) in travelerConfigs) {
            var travelerPosition = new FixedVector3(X: (sourceOrigin.X + offset.X), Y: (sourceOrigin.Y + offset.Y), Z: (sourceOrigin.Z + offset.Z));

            foreach (var (sourceYawDegrees, destinationYawDegrees, yawLabel) in yawPairsDegrees) {
                var sourceFrame = BuildFrame(origin: sourceOrigin, yawDegrees: sourceYawDegrees);
                var destinationFrame = BuildFrame(origin: destinationOrigin, yawDegrees: destinationYawDegrees);
                var caseLabel = $"{configLabel} / {yawLabel}";

                var forward = WorldFrameIsometry.MapArrival(
                    destination: in destinationFrame,
                    source: in sourceFrame,
                    travelerPlanarVelocity: planarVelocity,
                    travelerPosition: travelerPosition,
                    travelerVerticalVelocity: verticalVelocity,
                    travelerYawRadians: travelerYaw);

                // Back out through the SAME pair, feeding the forward arrival in as the new traveler — exactly what
                // a player walking back through one door produces.
                var backward = WorldFrameIsometry.MapArrival(
                    travelerPosition: forward.Position,
                    travelerYawRadians: forward.YawRadians,
                    travelerPlanarVelocity: forward.PlanarVelocity,
                    travelerVerticalVelocity: forward.VerticalVelocity,
                    source: in destinationFrame,
                    destination: in sourceFrame);

                var positionErrorRaw = RawAbs(a: backward.Position, b: travelerPosition);
                var velocityErrorRaw = Math.Max(val1: RawAbs(a: backward.PlanarVelocity, b: planarVelocity), val2: RawAbs(a: backward.VerticalVelocity, b: verticalVelocity));
                // The yaw contract is not "returns to the original" — it is "returns to the original plus exactly one
                // full turn", because each leg's delta is the wrapped representative and the two sum to 2pi.
                var expectedFacing = FixedQuaternion.FromAxisAngle(angle: travelerYaw, axis: s_upAxis).Rotate(vector: s_forward);
                var actualFacing = FixedQuaternion.FromAxisAngle(axis: s_upAxis, angle: backward.YawRadians).Rotate(vector: s_forward);
                var facingErrorRaw = RawAbs(a: actualFacing, b: expectedFacing);

                Assert.True(condition: (positionErrorRaw <= PositionErrorBudgetRaw), userMessage: $"{caseLabel}: position round-trip error {positionErrorRaw} raw exceeds the {PositionErrorBudgetRaw}-raw budget");
                Assert.True(condition: (velocityErrorRaw <= VelocityErrorBudgetRaw), userMessage: $"{caseLabel}: velocity round-trip error {velocityErrorRaw} raw exceeds the {VelocityErrorBudgetRaw}-raw budget");
                Assert.True(condition: (facingErrorRaw <= SeamCorrespondenceBudgetRaw), userMessage: $"{caseLabel}: facing round-trip error {facingErrorRaw} raw exceeds the {SeamCorrespondenceBudgetRaw}-raw budget");
            }
        }
    }
    [Fact]
    public void PointAt_CentreOfFace_IsExactlyOrigin() {
        var frame = BuildFrame(origin: new FixedVector3(X: FixedQ4816.FromInteger(value: 4), Y: FixedQ4816.FromInteger(value: 2), Z: FixedQ4816.FromInteger(value: -3)), yawDegrees: 47.0);

        Assert.Equal(expected: frame.Origin, actual: frame.PointAt(u: FixedQ4816.Zero, v: FixedQ4816.Zero));
    }
}
