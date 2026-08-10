using Xunit;

using Puck.Maths;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldPortalArrivalMath.ComputeArrival"/> — the MAPPED ARRIVAL isometry (a portal
/// facet's <c>arrival: "mapped"</c>), the positional-continuity half of a seamless border crossing. Pure and
/// fixed-point, so this proves the math directly rather than through <c>Puck.World.WorldInstanceHost</c> (the
/// composition root, out of reach for this project — see <c>PortalSweepOriginLawTests</c>' own remarks for the same
/// "prove the primitive, not the orchestration" shape). The full scan/coalesce/transfer/arrival orchestration is
/// verified by RUNNING <c>Puck.World</c> (CLAUDE.md rule 3).
/// </summary>
public sealed class WorldPortalArrivalMathLawTests {
    private static readonly FixedVector3 s_upAxis = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedQ4816 s_flipRadians = FixedQ4816.FromDouble(value: Math.PI);

    private static FixedQ4816 DegreesToRadians(double degrees) => FixedQ4816.FromDouble(value: (degrees * (Math.PI / 180.0)));

    [Fact]
    public void ComputeArrival_DifferentYawPair_MapsPoseHeadingAndVelocityExactly() {
        // THE DISCRIMINATING CASE: source yaw (30°) and destination yaw (120°) differ, and NEITHER equals zero — a
        // same-yaw pair (or a pair where one side is zero) would hide two distinct defects: dropping the 180° flip
        // entirely (dest - source alone, with no flip, happens to equal dest - source + flip whenever the flip is
        // reduced away by a coincidence the SAME-yaw case invites), and forgetting to subtract the source yaw at all
        // (which a zero source yaw cannot distinguish from a correct subtraction). With 30°/120°, both defects
        // produce a visibly different heading from the correct one — see the NotEqual assertions below.
        var sourcePosition = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var sourceYaw = DegreesToRadians(degrees: 30.0);
        var destinationPosition = new FixedVector3(X: FixedQ4816.FromInteger(value: 5), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: 5));
        var destinationYaw = DegreesToRadians(degrees: 120.0);

        var travelerPosition = new FixedVector3(X: FixedQ4816.FromInteger(value: 2), Y: FixedQ4816.One, Z: FixedQ4816.Zero);
        var travelerYaw = FixedQ4816.Zero;
        var travelerPlanarVelocity = new FixedVector3(X: FixedQ4816.FromInteger(value: 3), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var travelerVerticalVelocity = -FixedQ4816.One;

        var mapped = WorldPortalArrivalMath.ComputeArrival(
            travelerPosition: travelerPosition,
            travelerYawRadians: travelerYaw,
            travelerPlanarVelocity: travelerPlanarVelocity,
            travelerVerticalVelocity: travelerVerticalVelocity,
            sourcePosition: sourcePosition,
            sourceYawRadians: sourceYaw,
            destinationPosition: destinationPosition,
            destinationYawRadians: destinationYaw
        );

        // HEADING — pure FixedQ4816 addition/subtraction, exactly reproducible with no trig involved: deltaYaw =
        // (dest - source) + 180°, arrivalYaw = traveler + deltaYaw.
        var expectedDeltaYaw = ((destinationYaw - sourceYaw) + s_flipRadians);
        var expectedYaw = (travelerYaw + expectedDeltaYaw);

        Assert.Equal(expected: expectedYaw, actual: mapped.YawRadians);
        // Defect A: drop the flip (deltaYaw = dest - source alone) — a 180°-off answer, not a coincidental match,
        // because sourceYaw != destinationYaw here.
        Assert.NotEqual(expected: (travelerYaw + (destinationYaw - sourceYaw)), actual: mapped.YawRadians);
        // Defect B: forget to subtract the source yaw (deltaYaw = dest + 180° alone) — visibly wrong because
        // sourceYaw != 0 here.
        Assert.NotEqual(expected: (travelerYaw + (destinationYaw + s_flipRadians)), actual: mapped.YawRadians);

        // POSITION/VELOCITY — rotated by the SAME deltaYaw the heading assertion just proved, reconstructed via the
        // primitive rotation directly (FixedQuaternion) rather than hand-typed sin/cos literals: deltaYaw is a
        // Math.PI-derived (irrational) fixed-point value, so a hand-computed "expected" sin/cos would not be
        // guaranteed to land on the SAME rounded result FixedQ4816.SinCos's own turn-domain reduction produces (see
        // its remarks: "maximum observed error is 0.51 ULP" — a real, if tiny, rounding budget). Fixed-point
        // determinism (same inputs -> bit-identical output, always) makes reconstructing via the SAME primitive a
        // legitimate independent check of ComputeArrival's documented formula, not a tautology: a defect changing
        // WHICH angle rotates the offset (source's own yaw alone, or none at all) would still fail this.
        var rotation = FixedQuaternion.FromAxisAngle(axis: s_upAxis, angle: expectedDeltaYaw);
        var expectedOffset = rotation.Rotate(vector: (travelerPosition - sourcePosition));
        var expectedPosition = (destinationPosition + expectedOffset);
        var expectedPlanarVelocity = rotation.Rotate(vector: travelerPlanarVelocity);

        Assert.Equal(expected: expectedPosition, actual: mapped.Position);
        Assert.Equal(expected: expectedPlanarVelocity, actual: mapped.PlanarVelocity);
        // VERTICAL — untouched by a world-up rotation; preserved relative to the SOURCE face's own origin, carried
        // straight onto the destination face's origin. Non-trivial: source Y (0), destination Y (0) and traveler Y
        // (1) are not all equal, and destination X/Z (5,5) is nonzero, so this is not vacuously satisfied by an
        // implementation that ignores position/frames entirely.
        Assert.Equal(expected: (destinationPosition.Y + (travelerPosition.Y - sourcePosition.Y)), actual: mapped.Position.Y);
        Assert.Equal(expected: travelerVerticalVelocity, actual: mapped.VerticalVelocity);
    }

    [Fact]
    public void ComputeArrival_SameYawPair_StillAppliesThe180Flip() {
        // A same-yaw pair is NOT this suite's discriminating case (see the DifferentYawPair law's own remarks for
        // why) — but it is still a genuine, useful sanity check: even when source and destination frames share ONE
        // yaw, the flip must still turn the traveler around exactly 180°, proving the flip is unconditional rather
        // than something that only happens to fire when the frames disagree.
        var sourcePosition = FixedVector3.Zero;
        var destinationPosition = FixedVector3.Zero;
        var sharedYaw = DegreesToRadians(degrees: 45.0);
        var travelerYaw = FixedQ4816.Zero;

        var mapped = WorldPortalArrivalMath.ComputeArrival(
            travelerPosition: FixedVector3.Zero,
            travelerYawRadians: travelerYaw,
            travelerPlanarVelocity: FixedVector3.Zero,
            travelerVerticalVelocity: FixedQ4816.Zero,
            sourcePosition: sourcePosition,
            sourceYawRadians: sharedYaw,
            destinationPosition: destinationPosition,
            destinationYawRadians: sharedYaw
        );

        // dest - source cancels to zero when the yaws match, leaving exactly the flip.
        Assert.Equal(expected: (travelerYaw + s_flipRadians), actual: mapped.YawRadians);
        Assert.NotEqual(expected: travelerYaw, actual: mapped.YawRadians);
    }

    [Fact]
    public void ComputeArrival_ZeroDeltaYaw_LeavesPlanarVelocityAndOffsetUnrotated() {
        // The identity control: deltaYaw = 0 exactly when destinationYaw - sourceYaw = -180° (the flip cancels the
        // frame difference), so a traveler's offset and planar velocity pass through unrotated — the discriminating
        // case's own control, proving the rotation is not a fixed no-op the law above could not have detected.
        var sourceYaw = DegreesToRadians(degrees: 40.0);
        var destinationYaw = DegreesToRadians(degrees: -140.0);
        var offset = new FixedVector3(X: FixedQ4816.FromInteger(value: 4), Y: FixedQ4816.FromInteger(value: 2), Z: FixedQ4816.FromInteger(value: -3));
        var velocity = new FixedVector3(X: FixedQ4816.FromInteger(value: 1), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: -2));

        var mapped = WorldPortalArrivalMath.ComputeArrival(
            travelerPosition: offset,
            travelerYawRadians: FixedQ4816.Zero,
            travelerPlanarVelocity: velocity,
            travelerVerticalVelocity: FixedQ4816.Zero,
            sourcePosition: FixedVector3.Zero,
            sourceYawRadians: sourceYaw,
            destinationPosition: FixedVector3.Zero,
            destinationYawRadians: destinationYaw
        );

        Assert.Equal(expected: offset, actual: mapped.Position);
        Assert.Equal(expected: velocity, actual: mapped.PlanarVelocity);
    }

    // ---- Reciprocal round-trip error budget (adversarial-review finding 3) ----
    //
    // A→B then B→A is NOT proven to be an exact identity by this suite — ComputeArrival composes rotations through
    // FixedQuaternion.FromAxisAngle/Rotate, which resolve angles through FixedQ4816.SinCos's own turn-domain
    // reduction (see that method's remarks: "maximum observed error is 0.51 ULP", a real per-call rounding budget).
    // Two crossings compose two such rotations, so a round trip is a REAL fixed-point drift, not a rounding artifact
    // to chase to zero — the contract this law pins is an EXPLICIT UPPER BOUND on that drift, measured directly
    // rather than assumed, so a future change to the isometry (or to FixedQ4816's own trig tables) that silently
    // widens the drift is caught here rather than discovered downstream as an occasional "why did I not land exactly
    // where I started" bug report.
    //
    // MEASURED MAXIMA across every (yaw pair x traveler config) combination below (30 cases, all fixed data, no
    // randomness): position 14 raw units, velocity (planar or vertical, whichever is larger) 116 raw units, yaw 1
    // raw unit — CONSTANT across every case, never 0 and never more than 1, because it comes from a SEPARATE,
    // data-independent source: ComputeArrival's own s_flipRadians is FixedQ4816.FromDouble(Math.PI), rounded once,
    // and a round trip accumulates it TWICE (deltaYaw1 + deltaYaw2 = 2*s_flipRadians exactly, fixed-point addition
    // being exact) — which differs from FixedQ4816.FromDouble(2*Math.PI) (this law's own s_twoPiRadians) by the
    // ONE raw unit two independent single-Pi roundings can drift from one double-2Pi rounding. This is accepted,
    // documented on ComputeArrival itself: the drift is deterministic (bit-identical every run), sub-arcsecond per
    // crossing (1 raw unit is 1/65536 radian ~ 0.00087°), and yaw is never normalized or fed to anything that
    // requires it to be — see WorldBody's own unbounded-yaw convention.
    //
    // BUDGETS (asserted below, with headroom over the measured maxima so a future data point added to the spread
    // does not immediately flake, while still catching a real regression): position <= 32 raw units (~2.3x the
    // measured 14), velocity <= 160 raw units (~1.4x the measured 116 — velocity error scales with the ROTATED
    // vector's own magnitude, and the "high velocity" config below already stresses a Kart-plausible ~34 u/s), yaw
    // <= 2 raw units (2x the measured, constant 1). A budget tightened below what THIS suite measures would be
    // silently loosening the contract to make it look stricter — these are the tightest round numbers that clear
    // the actual measured maxima above, not numbers picked to look nice.
    private static readonly FixedQ4816 s_twoPiRadians = FixedQ4816.FromDouble(value: (2.0 * Math.PI));
    private const long PositionErrorBudgetRaw = 32;
    private const long VelocityErrorBudgetRaw = 160;
    private const long YawErrorBudgetRaw = 2;

    private static long RawAbs(FixedQ4816 a, FixedQ4816 b) => Math.Abs(a.Value - b.Value);

    [Fact]
    public void ComputeArrival_ReciprocalRoundTrip_StaysWithinTheMeasuredErrorBudget() {
        // The yaw-pair spread — INCLUDES the wrap-crossing pair the finding specifically calls out (source/destination
        // yaws straddling the +-180 degree seam), alongside same-yaw, opposite, near-wrap, and several non-round
        // pairs so the budget is not accidentally tuned to only the tidy cases.
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

        // Three traveler configurations spanning the physically reachable envelope: a moderate pose/speed, a
        // trigger-volume-bound offset (the derived WorldFaceFrame band — width/height from the frame itself, depth
        // max(frame half-depth, WorldFacePortalPolicy.CrossingFloor) — never a fixed Portal*/FrontDepth/HalfHeight
        // constant, which this suite predates) paired with a Kart-plausible high speed, and a near-rest traveler —
        // the combination that produced this suite's own measured maxima above was the trigger-bound-offset/
        // high-velocity config against the wide-spread yaw pair.
        var travelerConfigs = new (FixedVector3 Offset, FixedVector3 PlanarVelocity, FixedQ4816 VerticalVelocity, string Label)[] {
            (new FixedVector3(X: FixedQ4816.FromDouble(value: 2.4), Y: FixedQ4816.FromDouble(value: 1.8), Z: FixedQ4816.FromDouble(value: -2.9)),
                new FixedVector3(X: FixedQ4816.FromDouble(value: 12.0), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: -9.0)),
                FixedQ4816.FromDouble(value: -4.5), "moderate"),
            (new FixedVector3(X: FixedQ4816.FromDouble(value: 1.4), Y: FixedQ4816.FromDouble(value: 1.9), Z: FixedQ4816.FromDouble(value: 1.9)),
                new FixedVector3(X: FixedQ4816.FromDouble(value: 28.0), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: -19.0)),
                FixedQ4816.FromDouble(value: -9.0), "trigger-bound offset, high velocity"),
            (new FixedVector3(X: FixedQ4816.FromDouble(value: -1.5), Y: FixedQ4816.FromDouble(value: -1.9), Z: FixedQ4816.FromDouble(value: 0.1)),
                new FixedVector3(X: FixedQ4816.FromDouble(value: 0.5), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: 0.3)),
                FixedQ4816.FromDouble(value: 0.1), "small everything"),
        };

        var sourcePosition = new FixedVector3(X: FixedQ4816.FromDouble(value: 12.0), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: -18.0));
        var destinationPosition = new FixedVector3(X: FixedQ4816.FromDouble(value: -40.0), Y: FixedQ4816.FromDouble(value: 2.0), Z: FixedQ4816.FromDouble(value: 55.0));
        var travelerYaw = DegreesToRadians(degrees: 15.0);

        foreach (var (offset, planarVelocity, verticalVelocity, configLabel) in travelerConfigs) {
            var travelerPosition = new FixedVector3(X: (sourcePosition.X + offset.X), Y: (sourcePosition.Y + offset.Y), Z: (sourcePosition.Z + offset.Z));

            foreach (var (sourceYawDegrees, destinationYawDegrees, yawLabel) in yawPairsDegrees) {
                var sourceYaw = DegreesToRadians(degrees: sourceYawDegrees);
                var destinationYaw = DegreesToRadians(degrees: destinationYawDegrees);
                var caseLabel = $"{configLabel} / {yawLabel}";

                // A -> B.
                var forward = WorldPortalArrivalMath.ComputeArrival(
                    travelerPosition: travelerPosition,
                    travelerYawRadians: travelerYaw,
                    travelerPlanarVelocity: planarVelocity,
                    travelerVerticalVelocity: verticalVelocity,
                    sourcePosition: sourcePosition,
                    sourceYawRadians: sourceYaw,
                    destinationPosition: destinationPosition,
                    destinationYawRadians: destinationYaw
                );

                // B -> A, feeding the forward arrival straight back in as the new "traveler" pose/velocity —
                // exactly what a player walking back out through the SAME door pair would produce.
                var backward = WorldPortalArrivalMath.ComputeArrival(
                    travelerPosition: forward.Position,
                    travelerYawRadians: forward.YawRadians,
                    travelerPlanarVelocity: forward.PlanarVelocity,
                    travelerVerticalVelocity: forward.VerticalVelocity,
                    sourcePosition: destinationPosition,
                    sourceYawRadians: destinationYaw,
                    destinationPosition: sourcePosition,
                    destinationYawRadians: sourceYaw
                );

                var positionErrorRaw = Math.Max(RawAbs(a: backward.Position.X, b: travelerPosition.X), Math.Max(RawAbs(a: backward.Position.Y, b: travelerPosition.Y), RawAbs(a: backward.Position.Z, b: travelerPosition.Z)));
                var velocityErrorRaw = Math.Max(RawAbs(a: backward.PlanarVelocity.X, b: planarVelocity.X), Math.Max(RawAbs(a: backward.PlanarVelocity.Y, b: planarVelocity.Y), Math.Max(RawAbs(a: backward.PlanarVelocity.Z, b: planarVelocity.Z), RawAbs(a: backward.VerticalVelocity, b: verticalVelocity))));
                // The yaw contract is NOT "returns to the original" — it is "returns to the original plus exactly
                // one 2pi increment" (see this fact's own remarks on why: two independent single-Pi roundings
                // doubled vs. one double-2Pi rounding).
                var expectedYaw = new FixedQ4816(Value: (travelerYaw.Value + s_twoPiRadians.Value));
                var yawErrorRaw = RawAbs(a: backward.YawRadians, b: expectedYaw);

                Assert.True(condition: (positionErrorRaw <= PositionErrorBudgetRaw), userMessage: $"{caseLabel}: position round-trip error {positionErrorRaw} raw exceeds the {PositionErrorBudgetRaw}-raw budget");
                Assert.True(condition: (velocityErrorRaw <= VelocityErrorBudgetRaw), userMessage: $"{caseLabel}: velocity round-trip error {velocityErrorRaw} raw exceeds the {VelocityErrorBudgetRaw}-raw budget");
                Assert.True(condition: (yawErrorRaw <= YawErrorBudgetRaw), userMessage: $"{caseLabel}: yaw round-trip error {yawErrorRaw} raw (vs original + 2pi) exceeds the {YawErrorBudgetRaw}-raw budget");
            }
        }
    }
}
