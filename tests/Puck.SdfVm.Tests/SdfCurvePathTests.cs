using System.Numerics;

using Xunit;

using Puck.Maths;
using Puck.SdfVm.Views;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Binds the float twin (<see cref="SdfCurvePath"/>) against the fixed authority
/// (<see cref="Puck.Maths.CurvatureSpline"/>/<see cref="CompiledCurvatureSpline"/>) it converts from and must never
/// diverge far from, and proves the camera <c>path</c> op end to end through
/// <see cref="SdfCameraProgramEvaluator"/>: fraction resolution, wrap/clamp, the sampled subject pose, and
/// composition with <see cref="SdfCameraOp.LookAt"/>/<see cref="SdfCameraOp.Orbit"/>. Nothing here constructs a
/// document — the curve rows are built directly against <see cref="Puck.Maths.CurvatureSpline.Compile"/>, the same
/// fixed authority <c>WorldCurveRow.Compiled</c> reads.
/// </summary>
public sealed class SdfCurvePathTests {
    private static CurvatureSplineKnot Knot(double x, double z, double elevation, double tangentYaw, double curvature) => new(
        Curvature: FixedQ4816.FromDouble(value: curvature),
        Elevation: FixedQ4816.FromDouble(value: elevation),
        TangentYaw: FixedQ4816.FromDouble(value: tangentYaw),
        X: FixedQ4816.FromDouble(value: x),
        Z: FixedQ4816.FromDouble(value: z)
    );
    // Knots sampled on a circle of the given radius, evenly spaced by turnRadians — a circle's own curvature (signed
    // 1/radius under the cross2 convention) and tangent direction (perpendicular to the radius, at knotIndex ·
    // turnRadians + π/2) are exact by construction, so this exercises the general (w != 0) tangent-length branch
    // with a curvature every segment can actually reach.
    private static CurvatureSplineKnot CircleKnot(float radius, float elevation, int knotIndex, float turnRadians, float signedCurvature) {
        var angle = (knotIndex * turnRadians);

        return Knot(
            x: (radius * MathF.Cos(angle)),
            z: (radius * MathF.Sin(angle)),
            elevation: elevation,
            tangentYaw: (angle + (MathF.PI / 2f)),
            curvature: signedCurvature
        );
    }
    // A curve engineered so TotalLengthRaw well exceeds 2^24 arc units (past the point a float ULP exceeds a legal
    // short segment) while its LAST segment is short: twenty knots around a circle at
    // CurvatureSpline.MaxCoordinate's own radius (120-degree steps, CircleKnot/ClosedCurve's own proven angle),
    // ALL declaring zero curvature — the "w != 0, both curvatures zero" closed form (l0 = -s1/w, l1 = s0/w), never
    // the general quartic branch, so this compiles from direct rational division alone regardless of how large the
    // coordinates are — plus one short final knot in the last big knot's own tangent frame, the SAME (chord,
    // 90-degree-turn) shape curvature-spline.degenerate-branches' "w != 0, both kappa = 0" case already proves
    // admissible, scaled down to a short chord. Curvature is zero throughout, so the huge-to-short transition
    // carries no curvature-jump risk; the short knot's tangent genuinely turns 90 degrees from the last big knot's
    // (not an attempted exact parallel), so it carries no fragile transcendental-rounding risk either.
    private static CompiledCurvatureSpline SpiralWithShortTail() {
        var bigRadius = ((float)(double)CurvatureSpline.MaxCoordinate);
        const float bigTurn = ((2f * MathF.PI) / 3f); // 120 degrees per knot.
        const int bigKnotCount = 20;

        var knots = new List<CurvatureSplineKnot>(capacity: (bigKnotCount + 1));

        for (var i = 0; (i < bigKnotCount); i++) {
            knots.Add(CircleKnot(radius: bigRadius, elevation: 0f, knotIndex: i, turnRadians: bigTurn, signedCurvature: 0f));
        }

        var lastAngle = ((bigKnotCount - 1) * bigTurn);
        var lastPosition = new Vector2((bigRadius * MathF.Cos(lastAngle)), (bigRadius * MathF.Sin(lastAngle)));
        var lastYaw = (lastAngle + (MathF.PI / 2f));
        var forward = new Vector2(MathF.Cos(lastYaw), MathF.Sin(lastYaw));
        var perpendicular = new Vector2(-MathF.Sin(lastYaw), MathF.Cos(lastYaw));
        var tailPosition = (lastPosition + (0.15f * forward) + (0.2f * perpendicular));

        knots.Add(Knot(x: tailPosition.X, z: tailPosition.Y, elevation: 0f, tangentYaw: (lastYaw + (MathF.PI / 2f)), curvature: 0f));

        return CurvatureSpline.Compile(closed: false, knots: [.. knots]);
    }

    private static CompiledCurvatureSpline OpenCurve() {
        const float radius = 5f;
        const float curvature = (1f / radius);
        var turn = (MathF.PI / 6f); // 30 degrees per knot

        return CurvatureSpline.Compile(
            closed: false,
            knots: [
                CircleKnot(radius: radius, elevation: 0f, knotIndex: 0, turnRadians: turn, signedCurvature: curvature),
                CircleKnot(radius: radius, elevation: 1f, knotIndex: 1, turnRadians: turn, signedCurvature: curvature),
                CircleKnot(radius: radius, elevation: -0.5f, knotIndex: 2, turnRadians: turn, signedCurvature: curvature),
            ]
        );
    }
    private static CompiledCurvatureSpline ClosedCurve() {
        const float radius = 5f;
        const float curvature = (1f / radius);
        var turn = ((2f * MathF.PI) / 3f); // a full circle in three 120-degree arcs

        return CurvatureSpline.Compile(
            closed: true,
            knots: [
                CircleKnot(radius: radius, elevation: 0f, knotIndex: 0, turnRadians: turn, signedCurvature: curvature),
                CircleKnot(radius: radius, elevation: 0.5f, knotIndex: 1, turnRadians: turn, signedCurvature: curvature),
                CircleKnot(radius: radius, elevation: 0f, knotIndex: 2, turnRadians: turn, signedCurvature: curvature),
            ]
        );
    }

    // The engine facing convention SdfCurvePath.Sample's own remarks cite: facing(yaw) = (-sin(yaw), -cos(yaw)) in
    // world X/Z. Recovers the yaw a fixed sample's own tangent implies, for comparison against the float twin's
    // TangentYaw.
    private static float ExpectedYaw(FixedVector3 fixedTangent) => MathF.Atan2(
        x: -(float)(double)fixedTangent.Z,
        y: -(float)(double)fixedTangent.X
    );

    private static void AssertAgreesAlongTheWholeArc(CompiledCurvatureSpline compiled, float[] arcLengthsToSample) {
        var twin = new SdfCurvePath(compiled: compiled);

        Assert.Equal(expected: compiled.Closed, actual: twin.Closed);
        Assert.Equal(expected: (float)(double)compiled.TotalLength, actual: twin.TotalLength, precision: 4);

        foreach (var arcLength in arcLengthsToSample) {
            var fixedSample = compiled.Evaluate(arcLength: FixedQ4816.FromDouble(value: arcLength));
            var (floatPosition, floatYaw) = twin.Sample(arcLength: arcLength);
            var expectedPosition = new Vector3(
                x: (float)(double)fixedSample.Position.X,
                y: (float)(double)fixedSample.Position.Y,
                z: (float)(double)fixedSample.Position.Z
            );

            Assert.True(
                (floatPosition - expectedPosition).Length() < 5e-3f,
                $"arcLength={arcLength}: float={floatPosition} fixed={expectedPosition}"
            );

            var expectedYaw = ExpectedYaw(fixedTangent: fixedSample.Tangent);
            var yawDelta = MathF.Abs(MathF.IEEERemainder(x: (floatYaw - expectedYaw), y: (2f * MathF.PI)));

            Assert.True(
                yawDelta < 1e-2f,
                $"arcLength={arcLength}: floatYaw={floatYaw} expectedYaw={expectedYaw}"
            );
        }
    }

    [Fact]
    public void FloatTwinAgreesWithFixedEvaluateAlongAnOpenCurve() {
        var compiled = OpenCurve();
        var totalLength = (float)(double)compiled.TotalLength;
        var samples = new List<float>();

        for (var index = 0; (index <= 40); index++) {
            samples.Add(((totalLength * index) / 40f));
        }

        AssertAgreesAlongTheWholeArc(compiled: compiled, arcLengthsToSample: [.. samples]);
    }
    [Fact]
    public void FloatTwinAgreesWithFixedEvaluateAlongAClosedCurveIncludingTheWrap() {
        var compiled = ClosedCurve();
        var totalLength = (float)(double)compiled.TotalLength;
        var samples = new List<float>();

        for (var index = 0; (index <= 40); index++) {
            samples.Add(((totalLength * index) / 40f));
        }

        // Past-the-end and negative arc lengths both wrap — the fixed and float twins must agree there too.
        samples.Add(-totalLength * 0.25f);
        samples.Add(totalLength * 1.25f);
        samples.Add(totalLength);

        AssertAgreesAlongTheWholeArc(compiled: compiled, arcLengthsToSample: [.. samples]);
    }
    [Fact]
    public void FloatTwinTracksPositionAcrossAMassiveAccumulatedStationDownToAShortFinalSegment() {
        var compiled = SpiralWithShortTail();

        Assert.True(condition: (compiled.TotalLengthRaw > (1L << 56)), userMessage: $"fixture no longer accumulates past 2^24 arc units (TotalLengthRaw={compiled.TotalLengthRaw}); re-engineer SpiralWithShortTail.");

        var tailSegment = compiled.GetSegment(index: (compiled.SegmentCount - 1));

        Assert.True(condition: (tailSegment.LengthRaw < (16L << 32)), userMessage: $"the fixture's own final segment is no longer short (LengthRaw={tailSegment.LengthRaw}); re-engineer SpiralWithShortTail.");

        var twin = new SdfCurvePath(compiled: compiled);
        // The RETURNED position is float (Vector3), and the REQUESTED arc length is float too (Sample's own public
        // signature) — neither can carry sub-unit precision at a station in the tens of millions, so two DIFFERENT
        // requests within the short final segment itself collapse to the identical float value regardless of how
        // precisely the twin's own internals round; that ceiling belongs to the public float API, not to this fix.
        // What IS provable, and what the retired all-float twin's internal Station/lookup arithmetic risked getting
        // wrong, is SEGMENT IDENTITY across the whole accumulated range: sampling at well-separated big-knot
        // stations, and at the curve's very end (inside the short tail), still resolves near the fixed authority's
        // own answer — never a jump to an unrelated, distant segment.
        var slack = ((MathF.Abs(twin.TotalLength) * 2e-6f) + 32f);

        void AssertNear(long arcRaw, string label) {
            var fixedSample = compiled.EvaluateRaw(arcRaw: arcRaw);
            var arcLength = ((float)(arcRaw / 4294967296.0));
            var (floatPosition, _) = twin.Sample(arcLength: arcLength);
            var expectedPosition = new Vector3(
                x: (float)(double)fixedSample.Position.X,
                y: (float)(double)fixedSample.Position.Y,
                z: (float)(double)fixedSample.Position.Z
            );

            Assert.True(
                (floatPosition - expectedPosition).Length() < slack,
                $"{label} (arc raw {arcRaw}): float={floatPosition} fixed={expectedPosition} slack={slack}"
            );
        }

        for (var segmentIndex = 0; (segmentIndex < compiled.SegmentCount); segmentIndex += 4) {
            AssertNear(arcRaw: compiled.GetSegment(index: segmentIndex).StationRaw, label: $"segment {segmentIndex}'s own station");
        }

        AssertNear(arcRaw: tailSegment.StationRaw, label: "the short tail's own start");
        AssertNear(arcRaw: compiled.TotalLengthRaw, label: "the curve's own end (inside the short tail)");
    }

    [Fact]
    public void OpenCurveClampsPastEitherEnd() {
        var twin = new SdfCurvePath(compiled: OpenCurve());
        var (atZero, _) = twin.Sample(arcLength: 0f);
        var (belowZero, _) = twin.Sample(arcLength: -5f);
        var (atEnd, _) = twin.Sample(arcLength: twin.TotalLength);
        var (pastEnd, _) = twin.Sample(arcLength: (twin.TotalLength + 5f));

        Assert.Equal(expected: atZero, actual: belowZero);
        Assert.Equal(expected: atEnd, actual: pastEnd);
    }
    [Fact]
    public void ClosedCurveWrapsRatherThanClamping() {
        var twin = new SdfCurvePath(compiled: ClosedCurve());
        var (justPastEnd, _) = twin.Sample(arcLength: (twin.TotalLength + 1f));
        var (nearStart, _) = twin.Sample(arcLength: 1f);

        Assert.True((justPastEnd - nearStart).Length() < 1e-3f);
    }

    private static SdfCameraProgramSet PathProgram(SdfCurvePath curve, params SdfCameraOp[] trailingOps) => new(Programs: [
        new SdfCameraProgram(
            Name: "dolly",
            Operations: [
                new SdfCameraOp.Path(Curve: curve, Fraction: SdfCameraScalar.FromLiteral(value: 0.5f)),
                .. trailingOps,
                new SdfCameraOp.Fov(FieldOfViewRadians: SdfCameraScalar.FromLiteral(value: OrbitRig.DefaultFieldOfViewRadians)),
            ]
        ),
    ]);

    [Fact]
    public void PathOpSeedsTheEyeAtTheSampledPositionFacingTheCurveTangent() {
        var compiled = OpenCurve();
        var twin = new SdfCurvePath(compiled: compiled);
        var rig = new SdfCameraProgramRig(programs: PathProgram(curve: twin), scalarCount: 0, subjectCount: 0);
        var reference = new SdfAnchor(Position: new Vector3(x: 100f, y: 100f, z: 100f), Orientation: Quaternion.Identity);
        var clock = new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f);

        var (expectedPosition, expectedYaw) = twin.Sample(arcLength: (0.5f * twin.TotalLength));
        var pose = rig.ResolvePose(anchor: in reference, clock: in clock);

        Assert.Equal(expected: expectedPosition, actual: pose.Eye);

        var expectedForward = new Vector3(x: -MathF.Sin(expectedYaw), y: 0f, z: -MathF.Cos(expectedYaw));
        var actualForward = Vector3.Transform(value: -Vector3.UnitZ, rotation: Quaternion.CreateFromYawPitchRoll(yaw: expectedYaw, pitch: 0f, roll: 0f));

        Assert.True((actualForward - expectedForward).Length() < 1e-5f);
        // The default look-at (no explicit LookAt op) aims along the resolved subject's own forward — proving the
        // path op's orientation, not merely its position, feeds the evaluator.
        Assert.True(((pose.Target - pose.Eye) - (expectedForward * SdfCameraProgramEvaluator.DefaultFocusDistance)).Length() < 1e-4f);
    }
    [Fact]
    public void PathOpComposesWithOrbitPivotingTheSampledPoint() {
        var compiled = OpenCurve();
        var twin = new SdfCurvePath(compiled: compiled);
        var orbitOp = new SdfCameraOp.Orbit(
            AppliesLook: false,
            Distance: SdfCameraScalar.FromLiteral(value: 4f),
            PivotOffset: Vector3.Zero,
            Pitch: SdfCameraScalar.FromLiteral(value: 0f),
            Yaw: SdfCameraScalar.FromLiteral(value: 0f)
        );
        var rig = new SdfCameraProgramRig(programs: PathProgram(curve: twin, orbitOp), scalarCount: 0, subjectCount: 0);
        var reference = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        var clock = new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f);

        var (sampledPosition, _) = twin.Sample(arcLength: (0.5f * twin.TotalLength));
        var pose = rig.ResolvePose(anchor: in reference, clock: in clock);

        Assert.True((pose.Eye - (sampledPosition + OrbitRig.Offset(distance: 4f, pitch: 0f, yaw: 0f))).Length() < 1e-4f);
    }
    [Fact]
    public void PathOpFractionClampsOnAnOpenCurveControlWrapsOnAClosedOne() {
        var openTwin = new SdfCurvePath(compiled: OpenCurve());
        var closedTwin = new SdfCurvePath(compiled: ClosedCurve());
        var reference = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        var clock = new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f);

        var openAtEnd = new SdfCameraProgramRig(programs: PathAtFraction(curve: openTwin, fraction: 1f), scalarCount: 0, subjectCount: 0)
            .ResolvePose(anchor: in reference, clock: in clock);
        var openPastEnd = new SdfCameraProgramRig(programs: PathAtFraction(curve: openTwin, fraction: 1.5f), scalarCount: 0, subjectCount: 0)
            .ResolvePose(anchor: in reference, clock: in clock);

        Assert.Equal(expected: openAtEnd.Eye, actual: openPastEnd.Eye);

        var closedNearStart = new SdfCameraProgramRig(programs: PathAtFraction(curve: closedTwin, fraction: (1f / closedTwin.TotalLength)), scalarCount: 0, subjectCount: 0)
            .ResolvePose(anchor: in reference, clock: in clock);
        var closedJustPastEnd = new SdfCameraProgramRig(programs: PathAtFraction(curve: closedTwin, fraction: (1f + (1f / closedTwin.TotalLength))), scalarCount: 0, subjectCount: 0)
            .ResolvePose(anchor: in reference, clock: in clock);

        Assert.True((closedNearStart.Eye - closedJustPastEnd.Eye).Length() < 1e-3f);
        // Control: the open curve does NOT wrap the same way the closed one does — the near-start/past-end pair
        // would coincide there too if clamp and wrap were silently the same behavior.
        Assert.True(openAtEnd.Eye != new SdfCurvePath(compiled: OpenCurve()).Sample(arcLength: 0f).Position);
    }

    private static SdfCameraProgramSet PathAtFraction(SdfCurvePath curve, float fraction) => new(Programs: [
        new SdfCameraProgram(
            Name: "dolly",
            Operations: [
                new SdfCameraOp.Path(Curve: curve, Fraction: SdfCameraScalar.FromLiteral(value: fraction)),
                new SdfCameraOp.Fov(FieldOfViewRadians: SdfCameraScalar.FromLiteral(value: OrbitRig.DefaultFieldOfViewRadians)),
            ]
        ),
    ]);
}
