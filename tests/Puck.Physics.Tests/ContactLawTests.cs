using Puck.Physics.Tests.Fixtures;
using Puck.Physics.Tests.Geometry;
using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// The contact-generation and association laws. Each pair is a mechanism and its sabotage: the sabotaged run is the
/// evidence that the law is testing something, and it is expressed as a solver OPTION so the red is a change of
/// mechanism rather than a change of expectation.
/// </summary>
public sealed class ContactLawTests {
    [Fact]
    public void SphereRestingInACornerKeepsTwoPersistentContactsWithDistinctNormals() {
        var world = SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(rateHz: 60, substepCount: 4));

        world.Advance(count: 240);

        Assert.Equal(expected: 0, actual: world.Solver.RefusalCount);
        Assert.Equal(expected: 2, actual: world.Slots.ActiveCount);

        var floor = FindSlot(sourceId: SpikeFixtures.FloorSourceId, world: world);
        var wall = FindSlot(sourceId: SpikeFixtures.WallSourceId, world: world);

        // Distinct normals, both carrying a real accumulated impulse: the corner is two constraints, not one.
        Assert.True(condition: (floor.Normal.Y > FixedQ4816.FromDouble(value: 0.99d)), userMessage: "the floor contact's normal must be the floor's");
        Assert.True(condition: (wall.Normal.X > FixedQ4816.FromDouble(value: 0.99d)), userMessage: "the wall contact's normal must be the wall's");
        Assert.True(condition: (floor.NormalImpulseRaw > 0L), userMessage: "the floor contact must carry an accumulated impulse");
        Assert.True(condition: (wall.NormalImpulseRaw > 0L), userMessage: "the wall contact must carry an accumulated impulse");

        // The sphere rests exactly one radius from both surfaces, to within the soft constraint's own slop.
        AssertNear(actual: world.Pose.Center.X, expected: 0.5d, tolerance: 0.002d, subject: "resting distance from the wall");
        AssertNear(actual: world.Pose.Center.Y, expected: 0.5d, tolerance: 0.002d, subject: "resting distance from the floor");
    }
    [Fact]
    public void KeyingASlotByTheBodyFeatureAloneCollapsesTheCornerAndLosesTheFloor() {
        var world = SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(rateHz: 60, substepCount: 4, compositeIdentity: false));

        world.Advance(count: 240);

        // Both corner candidates are the sphere's only feature, so the second overwrites the first and exactly one
        // constraint survives; the surface it does not name stops holding the body at all.
        Assert.Equal(expected: 1, actual: world.Slots.ActiveCount);
        Assert.True(
            condition: (world.Pose.Center.Y < FixedQ4816.Zero),
            userMessage: $"the aliased corner must lose the floor contact and let the sphere fall; it rested at y={MeasurementReport.Format(value: world.Pose.Center.Y)}"
        );
    }
    [Fact]
    public void CapsuleWaistAgainstAThinSlabIsCaughtWhileBothEndSpheresStayClear() {
        var world = SpikeFixtures.CapsuleWaist(
            options: new() { RateHz = 60, SubstepCount = 4, },
            mode: CapsuleWitnessMode.SegmentScan,
            surface: out var surface
        );

        world.Advance(count: 120);

        Assert.Equal(expected: 0, actual: world.Solver.RefusalCount);
        Assert.True(condition: (world.LastStepCandidateCount > 0), userMessage: "the segment scan must find the waist contact");
        Assert.True(
            condition: (surface.LastEndpointSeparation > FixedQ4816.FromDouble(value: 0.1d)),
            userMessage: $"both end spheres must stay clear of the slab; the nearer reported {MeasurementReport.Format(value: surface.LastEndpointSeparation)}"
        );
        Assert.True(
            condition: (world.Pose.Center.Y > FixedQ4816.FromDouble(value: 0.3d)),
            userMessage: $"the capsule must rest on the slab; it reached y={MeasurementReport.Format(value: world.Pose.Center.Y)}"
        );
    }
    [Fact]
    public void SamplingOnlyTheCapsuleEndpointsTunnelsThroughTheSlab() {
        var world = SpikeFixtures.CapsuleWaist(
            options: new() { RateHz = 60, SubstepCount = 4, },
            mode: CapsuleWitnessMode.EndpointsOnly,
            surface: out _
        );

        world.Advance(count: 120);

        Assert.True(
            condition: (world.Pose.Center.Y < FixedQ4816.FromDouble(value: -1d)),
            userMessage: $"a fixed endpoint recipe must miss the waist and let the capsule fall; it reached y={MeasurementReport.Format(value: world.Pose.Center.Y)}"
        );
    }
    [Fact]
    public void RotatingBoxSettlesOnThePlaneWithoutJitter() {
        var world = SpikeFixtures.RotatingBox(options: new() { RateHz = 60, SubstepCount = 4, });

        world.Advance(count: 180);

        var settled = world.Pose.Center.Y;
        var lowest = settled;
        var highest = settled;

        for (var step = 0; (step < 120); ++step) {
            world.Advance();
            lowest = FixedQ4816.Min(x: lowest, y: world.Pose.Center.Y);
            highest = FixedQ4816.Max(x: highest, y: world.Pose.Center.Y);
        }

        Assert.Equal(expected: 0, actual: world.Solver.RefusalCount);
        AssertNear(actual: settled, expected: 0.25d, subject: "resting height of the box", tolerance: 0.002d);

        // Jitter is the quantity being measured: a settled contact must not breathe by more than the fixed-point
        // resolution the pose is carried at.
        Assert.True(
            condition: ((highest - lowest) <= FixedQ4816.FromDouble(value: 0.0002d)),
            userMessage: $"the settled box moved between {MeasurementReport.Format(value: lowest)} and {MeasurementReport.Format(value: highest)}"
        );
        Assert.True(
            condition: (FixedQ4816.Abs(value: world.Body.AngularVelocity.Z) < FixedQ4816.FromDouble(value: 0.01d)),
            userMessage: $"the box's spin must be absorbed; it retained {MeasurementReport.Format(value: world.Body.AngularVelocity.Z)}"
        );
        Assert.True(
            condition: (world.LastStepCandidateCount >= 4),
            userMessage: "a box lying flat on a plane must present its whole face as candidates"
        );
    }
    [Fact]
    public void FixedSpeculativeActivationCatchesAFirstAppearanceApproachWithoutTunnelling() {
        var world = SpikeFixtures.HighSpeedApproach(
            options: new() { RateHz = 60, SubstepCount = 1, },
            height: 1d,
            downwardSpeed: 400d
        );

        world.Advance();

        Assert.Equal(expected: 0, actual: world.Solver.RefusalCount);
        Assert.Equal(expected: 1, actual: world.LastStepCandidateCount);

        // The body was a whole metre clear at the head of the step and would have travelled six and a half metres; the
        // conservatively rounded prediction is what lets the constraint exist at all.
        Assert.True(
            condition: (world.LastStepActivationBound > FixedQ4816.FromInteger(value: 6L)),
            userMessage: $"the swept activation bound was only {MeasurementReport.Format(value: world.LastStepActivationBound)}"
        );
        AssertNear(actual: world.Pose.Center.Y, expected: 0.1d, tolerance: 0.001d, subject: "height after the caught step");
    }
    [Fact]
    public void RemovingThePredictedBoundTunnelsThroughTheSurface() {
        var world = SpikeFixtures.HighSpeedApproach(
            options: new() { Activation = FixedSpeculativeActivation.CurrentOnly, RateHz = 60, SubstepCount = 1, },
            height: 1d,
            downwardSpeed: 400d
        );

        world.Advance();

        Assert.Equal(expected: 0, actual: world.LastStepCandidateCount);
        Assert.True(
            condition: (world.Pose.Center.Y < FixedQ4816.FromInteger(value: -1L)),
            userMessage: $"activation from the current separation alone must tunnel; the body reached y={MeasurementReport.Format(value: world.Pose.Center.Y)}"
        );
    }
    [Fact]
    public void NoConstraintAndNoImpulseAppearBeyondThePredictedBound() {
        var world = SpikeFixtures.HighSpeedApproach(
            options: new() { RateHz = 60, SubstepCount = 1, },
            height: 100d,
            downwardSpeed: 1d
        );

        world.Advance();

        Assert.Equal(expected: 0, actual: world.LastStepCandidateCount);
        Assert.Equal(expected: 0, actual: world.Slots.ActiveCount);
        Assert.Equal(expected: 0L, actual: world.Solver.LastStepMaximumImpulseRaw);
        Assert.True(
            condition: (world.LastStepActivationBound < FixedQ4816.FromDouble(value: 0.1d)),
            userMessage: $"a slow distant body's bound must stay near the authored margin; it was {MeasurementReport.Format(value: world.LastStepActivationBound)}"
        );
    }
    [Fact]
    public void AnInjectedDeepOverlapIsRecoveredAlongTheAuthoredEscapeDirection() {
        var world = SpikeFixtures.DeepOverlap(options: new() { RateHz = 60, SubstepCount = 4, });
        var firstStepImpulse = 0L;

        world.Advance();
        firstStepImpulse = world.Solver.LastStepMaximumImpulseRaw;
        world.Advance(count: 59);

        Assert.Equal(expected: 0, actual: world.Solver.RefusalCount);

        // The recovery path manufactures no normal: while the body is embedded it accumulates no impulse at all, and
        // the extraction is a bounded displacement instead.
        Assert.Equal(actual: firstStepImpulse, expected: 0L);
        Assert.True(
            condition: (world.Pose.Center.Y > FixedQ4816.FromDouble(value: 0.55d)),
            userMessage: $"the sphere must be extracted to the side it entered from; it reached y={MeasurementReport.Format(value: world.Pose.Center.Y)}"
        );
    }
    [Fact]
    public void WithoutTheRecoveryPathTheOrdinaryCorrectionDrivesTheBodyOutTheWrongSide() {
        var world = SpikeFixtures.DeepOverlap(options: new() { DeepRecovery = false, RateHz = 60, SubstepCount = 4, });

        world.Advance(count: 60);

        Assert.True(
            condition: (world.Pose.Center.Y < FixedQ4816.FromDouble(value: -0.55d)),
            userMessage: $"the nearest-surface normal points through the slab, so the correction clamp must expel the body downward; it reached y={MeasurementReport.Format(value: world.Pose.Center.Y)}"
        );
    }

    private static FixedManifoldSlot FindSlot(SpikeWorld world, int sourceId) {
        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref world.Slots[index];

            if (slot.Occupied && (slot.SourceId == sourceId)) {
                return slot;
            }
        }

        Assert.Fail(message: $"no slot is associated with surface {sourceId}");

        return default;
    }
    private static void AssertNear(FixedQ4816 actual, double expected, double tolerance, string subject) {
        var difference = Math.Abs(value: (((double)actual) - expected));

        Assert.True(condition: (difference <= tolerance), userMessage: $"{subject}: expected {expected}, measured {MeasurementReport.Format(value: actual)}");
    }
}
