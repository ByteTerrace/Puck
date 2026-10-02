using Puck.World.Protocol;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a refused sweep is a full block — the body does not move this tick. Its position, attitude,
/// velocity and grounded fact read exactly as before the step, the population narrates the refusal once and its
/// recovery once on the <c>body.sweep</c> channel, and <c>body.where</c> carries it while it holds. A refusal is local to
/// the refused body: a carrier under a refused carried body steps exactly as if it carried nothing, and a rigid body
/// refused across its substeps keeps the velocity and pose it began the tick with.
/// </summary>
[Collection(ConsoleRedirectionCollection.Name)]
public sealed class BodySweepRefusalLawTests {
    private const int ForwardOrdinal = 0;

    private static readonly FixedQ4816 Quarter = FixedQ4816.FromDouble(value: 0.25);

    // A lattice world whose seat kit carries a half-unit sphere: its core sits half a unit above the body's foot point.
    private static WorldDefinition LatticeWorld(bool rigid = false) {
        var source = Fixtures.BuildDocument();
        var world = source with {
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                Collider = new WorldCollider.Sphere(Radius: (rigid ? 0.15f : 0.5f)),
                BodyContact = (rigid ? WorldBodyContactMode.Solid : kit.BodyContact),
                Rigid = (rigid ? new WorldRigid(AngularDamping: 0f, Friction: 0f, LinearDamping: 0f, Mass: 0.3f, Restitution: 0f, RollingFriction: 0f) : kit.Rigid),
            })],
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
        };

        return Fixtures.WithLattice(
            definition: world,
            composite: new WorldFieldsSection(
                Lattice: new WorldFieldLatticeDefinition(Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 4, Depth: 4, Layers: 1, StepEveryTicks: 1),
                Fields: [new WorldFieldRow(Name: "ground", Min: 0f, Max: 10f, HeightScale: 1f, Color: "#808080")],
                Reactions: null
            )
        );
    }
    // The body's state a refused step must leave unchanged: the population's pose hash, its grounded fact, and its
    // velocity, which the pose hash does not cover for a walking body.
    private static (ulong Pose, bool Grounded, FixedVector3 Velocity) State(WorldFixture fixture, WorldBody body) =>
        (WorldReplaySnapshot.HashState(population: fixture.Server.Population), body.Grounded, body.ApproximateWorldVelocity());
    private static int Count(string narration, string needle) {
        var count = 0;

        for (var at = narration.IndexOf(comparisonType: StringComparison.Ordinal, value: needle); (at >= 0); at = narration.IndexOf(value: needle, startIndex: (at + needle.Length), comparisonType: StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }

    [Fact]
    public void ALatticeBodyRefusedAtTheCarriersEndDoesNotMoveAndIsNarratedOnceEachWay() {
        var original = Console.Error;
        using var captured = new StringWriter();

        try {
            Console.SetError(newError: captured);

            using var fixture = Fixtures.FreshServer(definition: LatticeWorld());
            var body = fixture.JoinSeat();

            // A quarter unit under the carrier's top: the body's core, half a unit above its foot, lies past it.
            body.Pose(pitchRadians: FixedQ4816.Zero, position: new FixedVector3(X: FixedQ4816.One, Y: (FixedQ4816.MaxValue - Quarter), Z: FixedQ4816.One), rollRadians: FixedQ4816.Zero, yawRadians: FixedQ4816.Zero);
            body.EnqueueRun(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One), seconds: 1f);

            var before = State(body: body, fixture: fixture);

            fixture.Step();

            Assert.Equal(expected: ContactRefusal.UnrepresentableSweep, actual: body.SweepRefusal);
            Assert.Equal(expected: before, actual: State(body: body, fixture: fixture));
            Assert.Contains(actualString: body.DescribeWhere(index: 0), expectedSubstring: "sweep=refused(unrepresentable)");

            fixture.Step();

            Assert.Equal(expected: before, actual: State(body: body, fixture: fixture));

            // Moved back into the carrier, the body steps again and recovers.
            body.Pose(pitchRadians: FixedQ4816.Zero, position: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.FromInteger(value: 3L), Z: FixedQ4816.One), rollRadians: FixedQ4816.Zero, yawRadians: FixedQ4816.Zero);
            fixture.Step();
            fixture.Step();

            Assert.Equal(expected: ContactRefusal.None, actual: body.SweepRefusal);
            Assert.DoesNotContain(actualString: body.DescribeWhere(index: 0), expectedSubstring: "sweep=");
        } finally {
            Console.SetError(newError: original);
        }

        var narration = captured.ToString();

        Assert.Equal(expected: 1, actual: Count(narration: narration, needle: "[body.sweep: body 0 refused"));
        Assert.Equal(expected: 1, actual: Count(narration: narration, needle: "[body.sweep: body 0 recovered"));
    }
    [Fact]
    public void ARigidBodyRefusedAcrossItsSubstepsKeepsTheVelocityAndPoseItBeganWith() {
        // A rigid ball rising at fifteen units a second takes seven substeps a tick. Posed a tenth of a unit under the
        // carrier's top, its core, 0.15 above its foot, already lies past it, so the first substep is refused: the
        // snapshot, taken before the tick's damping and gravity, must leave the velocity and the pose it began with.
        // (A later substep cannot be the first refused: a sweep's box encloses its core's start with the core's radius
        // and the travel, so it reaches the carrier's end first, and a ball rising toward the end is held by its sweep,
        // which proves no ground there, a substep before its core's start could leave the carrier.)
        using var fixture = Fixtures.FreshServer(definition: LatticeWorld(rigid: true));
        var ball = fixture.JoinSeat();

        ball.Pose(pitchRadians: FixedQ4816.Zero, position: new FixedVector3(X: FixedQ4816.Zero, Y: (FixedQ4816.MaxValue - FixedQ4816.FromDouble(value: 0.1)), Z: FixedQ4816.Zero), rollRadians: FixedQ4816.Zero, yawRadians: FixedQ4816.Zero);
        Assert.True(condition: ball.TryApplyRigidImpulse(impulse: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: 4.5), Z: FixedQ4816.Zero), velocityCeiling: FixedQ4816.FromInteger(value: 1000L)));

        var before = State(body: ball, fixture: fixture);
        var velocity = ball.RigidVelocity;

        fixture.Step();

        Assert.True(condition: (ball.RigidStaticSubstepsThisTick > 1), userMessage: $"the tick took {ball.RigidStaticSubstepsThisTick} substeps; the fixture needs several");
        Assert.Equal(expected: ContactRefusal.UnrepresentableSweep, actual: ball.SweepRefusal);
        Assert.Equal(expected: velocity, actual: ball.RigidVelocity);
        Assert.Equal(expected: before, actual: State(body: ball, fixture: fixture));
    }
    [Fact]
    public void ACarrierUnderARefusedCarriedBodyStepsAsIfItCarriedNothing() {
        // The carried ball is posed at the carrier's least x: the step to its carry point, at or past zero beside a
        // carrier near the origin, spans 2⁶³ raws or more, past what the carrier holds, so the ball's sweep is refused.
        // The carrier must not feel it.
        (FixedVector3 Carrier, ContactRefusal BallRefusal, FixedQ4816 BallX) Walk(bool carrying) {
            using var fixture = Fixtures.FreshServer(definition: WorldCarryTangibilityLawTests.WallCarryDocument(includeWall: true));
            var carrier = fixture.JoinSeat();
            var ball = fixture.Server.Body(index: WorldCarryTangibilityLawTests.BallIndex)!;

            if (carrying) {
                Assert.True(condition: fixture.Server.Population.TryBeginCarry(carrierIndex: WorldCarryTangibilityLawTests.CarrierIndex, reason: out var reason, targetIndex: WorldCarryTangibilityLawTests.BallIndex), userMessage: reason);
            }

            ball.Pose(pitchRadians: FixedQ4816.Zero, position: new FixedVector3(X: FixedQ4816.MinValue, Y: FixedQ4816.One, Z: FixedQ4816.Zero), rollRadians: FixedQ4816.Zero, yawRadians: FixedQ4816.Zero);
            carrier.EnqueueRun(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One), seconds: 1f);

            for (var tick = 0; (tick < 10); tick++) {
                fixture.Step();
            }

            return (carrier.FixedPosition, ball.SweepRefusal, ball.FixedPosition.X);
        }

        var free = Walk(carrying: false);
        var carried = Walk(carrying: true);

        Assert.NotEqual(expected: FixedVector3.Zero, actual: free.Carrier);
        Assert.Equal(actual: carried.Carrier, expected: free.Carrier);
        Assert.Equal(actual: carried.BallRefusal, expected: ContactRefusal.UnrepresentableSweep);
        Assert.Equal(expected: FixedQ4816.MinValue, actual: carried.BallX);
    }
}
