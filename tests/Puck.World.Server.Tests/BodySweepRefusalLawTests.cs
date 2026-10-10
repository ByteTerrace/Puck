using Puck.World.Protocol;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a refused sweep is a full block — the body does not move this tick. Its whole captured state
/// (integration residue, transfer state, pose and up) reads exactly as before the step, the population narrates the
/// refusal once and its recovery once on the <c>body.sweep</c> channel, and <c>body.where</c> carries it while it holds.
/// A refused body is immovable for the rest of its tick: a carrier under a refused carried body steps exactly as if it
/// carried nothing, a body overlapping it is resolved against it as static, and a rigid body refused across its
/// substeps keeps the velocity and pose it began the tick with.
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
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ARefusedStepLeavesTheBodysWholeCapturedStateAsItWas(bool shapedAtThirtyHertz) {
        // The lattice body under the carrier's top, walking forward under a tilted uniform gravity: phase 0 turns the
        // frame toward the field's up (0.6, 0.8, 0) before the contact phase refuses the step. At 30 Hz under a shaping
        // row whose Along.Engage is 1, the planar ramp accumulator integrates too. A refused step restores every field
        // it wrote, so the body's whole captured state (integration residue, transfer state, pose and up) reads after
        // the tick exactly as before it. Both records are described member by member, so a field added to either is
        // compared without a change here.
        var world = TiltedLatticeWorld();

        if (shapedAtThirtyHertz) {
            world = world with {
                KitRowsRaw = [.. world.Kits.Select(selector: kit => kit with { Motion = kit.Motion with { Shaping = [new WorldShaping(Along: new WorldShapingAlong(Engage: 1f))] } })],
                Simulation = new WorldSimulationDefaults(RateHz: 30),
            };
        }

        using var fixture = Fixtures.FreshServer(definition: world);
        var body = PoseRefusedWalker(at: new FixedVector3(X: FixedQ4816.One, Y: (FixedQ4816.MaxValue - Quarter), Z: FixedQ4816.One), body: fixture.JoinSeat());
        var before = WholeState(body: body);

        fixture.Step();

        Assert.Equal(expected: ContactRefusal.UnrepresentableSweep, actual: body.SweepRefusal);
        Assert.Equal(expected: before, actual: WholeState(body: body));
    }
    [Fact]
    public void TwoBodiesRefusedInTheSameTickEachRestoreTheirOwnState() {
        // The population owns one step scratch, which each body's step captures into in turn. Two bodies under the
        // carrier's top, at different places, with tapes of one segment and of three, are refused back to back for two
        // ticks: each must read after every tick exactly as it did before, its own state and never the other's. The
        // tapes' arrays differ in length, so a scratch reusing an array of the wrong length shows; the second tick is
        // the one where the scratch already holds the other body's arrays.
        using var fixture = Fixtures.FreshServer(definition: TiltedLatticeWorld());
        var first = PoseRefusedWalker(at: new FixedVector3(X: FixedQ4816.One, Y: (FixedQ4816.MaxValue - Quarter), Z: FixedQ4816.One), body: fixture.JoinSeat(slot: 0), segments: 1);
        var second = PoseRefusedWalker(at: new FixedVector3(X: FixedQ4816.FromInteger(value: 2L), Y: (FixedQ4816.MaxValue - FixedQ4816.FromDouble(value: 0.125)), Z: FixedQ4816.FromInteger(value: 3L)), body: fixture.JoinSeat(slot: 1), segments: 3);
        var firstBefore = WholeState(body: first);
        var secondBefore = WholeState(body: second);

        Assert.NotEqual(actual: secondBefore, expected: firstBefore);
        _ = Assert.Single(collection: first.CaptureTransferState().TapeIntents);
        Assert.Equal(expected: 3, actual: second.CaptureTransferState().TapeIntents.Count);

        for (var tick = 0; (tick < 2); tick++) {
            fixture.Step();

            Assert.Equal(expected: ContactRefusal.UnrepresentableSweep, actual: first.SweepRefusal);
            Assert.Equal(expected: ContactRefusal.UnrepresentableSweep, actual: second.SweepRefusal);
            Assert.Equal(expected: firstBefore, actual: WholeState(body: first));
            Assert.Equal(expected: secondBefore, actual: WholeState(body: second));
        }
    }
    [Fact]
    public void TheWholeStateDescriptionSeesEveryTapeChannel() {
        // A tape's intents carry their channels inline (PlayerIntent's ChannelValues is an InlineArray, which exposes no
        // property), so a description that reads properties alone reads every intent alike and could not see a tape
        // whose channels were corrupted. Two intents differing in one channel must describe differently.
        var resting = default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One);
        var turned = resting.WithChannel(ordinal: 3, value: FixedQ4816.One);

        Assert.NotEqual(expected: Describe(value: resting), actual: Describe(value: turned));
    }

    // The lattice world under a tilted uniform gravity, whose up is (0.6, 0.8, 0).
    private static WorldDefinition TiltedLatticeWorld() => LatticeWorld() with {
        GravityRaw = new WorldGravity(
            Attractors: [],
            GravitationalConstant: 0f,
            SofteningLength: 0.5f,
            Solver: WorldGravitySolver.Pairwise,
            Uniform: new DocumentVector3(x: -3f, y: -4f, z: 0f)
        ),
    };
    // Poses a walking body under the carrier's top, its core past it, with forward runs queued as that many tape segments,
    // each a second long and each speaking a different strafe, so the segments' channels differ.
    private static WorldBody PoseRefusedWalker(WorldBody body, FixedVector3 at, int segments = 1) {
        body.Pose(pitchRadians: FixedQ4816.Zero, position: at, rollRadians: FixedQ4816.Zero, yawRadians: FixedQ4816.Zero);

        for (var segment = 0; (segment < segments); segment++) {
            body.EnqueueRun(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One).WithChannel(ordinal: 1, value: (FixedQ4816.FromInteger(value: segment) / FixedQ4816.FromInteger(value: 4L))), seconds: 1f);
        }

        // The population admits each body to its tick (TryBeginOrdinaryAdvance) before the step begins. That latch is
        // the tick's, not the step's, and a refused body keeps it, so it is seeded here as the admission leaves it,
        // and the comparison is about the step alone.
        body.ApplyIntegrationResidue(residue: body.CaptureIntegrationResidue() with { OrdinaryAdvanceAdmitted = true });

        return body;
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
        var free = CarrierWalk(ballX: FixedQ4816.MinValue, carrying: false);
        var carried = CarrierWalk(ballX: FixedQ4816.MinValue, carrying: true);

        Assert.NotEqual(expected: FixedVector3.Zero, actual: free.Carrier);
        Assert.Equal(actual: carried.Carrier, expected: free.Carrier);
        Assert.Equal(actual: carried.BallRefusal, expected: ContactRefusal.UnrepresentableSweep);
        Assert.Equal(expected: FixedQ4816.MinValue, actual: carried.BallX);
    }
    [Fact]
    public void ACarrierUnderACarriedBodyWhoseSweepProvesNothingStepsAsIfItCarriedNothing() {
        // A quarter unit inside the carrier's least x, the step to the carry point fits the carrier, so it is not
        // refused; but its sweep's first box lies past the wall program's frame, so it proves nothing and the ball is
        // held at its start. Running out of proof is no physical block: a carrier correction comes only from a proven
        // contact, so the carrier must step as if it carried nothing.
        var quarterPast = (FixedQ4816.MinValue + Quarter);
        var free = CarrierWalk(ballX: quarterPast, carrying: false);
        var carried = CarrierWalk(ballX: quarterPast, carrying: true);

        Assert.Equal(actual: carried.Carrier, expected: free.Carrier);
        Assert.Equal(actual: carried.BallRefusal, expected: ContactRefusal.None);
        Assert.Equal(actual: carried.BallX, expected: quarterPast);
    }
    [Fact]
    public void ARefusedCarriedBodyIsImmovableForTheRestOfItsTickAndPushesNothingIntoItsCarrier() {
        // The carried ball posed at the carrier's least x is refused; a second, resting ball overlaps it from an eighth
        // of a unit away. The pair pass must resolve the second ball against the refused one as static: the refused
        // ball keeps its pose bit for bit, the second ball alone leaves the overlap, and the carrier, which falls under
        // its own kit's gravity, ends the tick exactly where it ends it when the second ball is posed far away.
        var overlapped = CarriedPairTick(otherZ: -FixedQ4816.FromDouble(value: 0.125));
        var apart = CarriedPairTick(otherZ: FixedQ4816.FromInteger(value: 50L));

        Assert.True(condition: overlapped.OverlappedBefore, userMessage: "the fixture must start the two balls in overlap");
        Assert.Equal(actual: overlapped.BallRefusal, expected: ContactRefusal.UnrepresentableSweep);
        Assert.Equal(actual: overlapped.BallAfter, expected: overlapped.BallBefore);
        Assert.Equal(actual: overlapped.Carrier, expected: apart.Carrier);
        Assert.False(condition: overlapped.OverlappedAfter, userMessage: "the second ball must be resolved out of the refused ball");
    }

    // One tick of a carry whose ball is posed at the carrier's least x, with a second, resting ball posed beside it at
    // the given z.
    private static ((FixedVector3, FixedQuaternion) BallBefore, (FixedVector3, FixedQuaternion) BallAfter, (FixedVector3, FixedQuaternion) Carrier, ContactRefusal BallRefusal, bool OverlappedBefore, bool OverlappedAfter) CarriedPairTick(FixedQ4816 otherZ) {
        using var fixture = Fixtures.FreshServer(definition: CarryFixtures.WallCarryDocument(includeOtherBody: true, includeWall: true));
        var carrier = fixture.JoinSeat();
        var ball = fixture.Server.Body(index: CarryFixtures.BallIndex)!;
        var other = fixture.Server.Body(index: (CarryFixtures.BallIndex + 1))!;

        Assert.True(condition: fixture.Server.Population.TryBeginCarry(carrierIndex: CarryFixtures.CarrierIndex, reason: out var reason, targetIndex: CarryFixtures.BallIndex), userMessage: reason);

        ball.Pose(pitchRadians: FixedQ4816.Zero, position: new FixedVector3(X: FixedQ4816.MinValue, Y: FixedQ4816.One, Z: FixedQ4816.Zero), rollRadians: FixedQ4816.Zero, yawRadians: FixedQ4816.Zero);
        other.Pose(pitchRadians: FixedQ4816.Zero, position: new FixedVector3(X: FixedQ4816.MinValue, Y: FixedQ4816.One, Z: otherZ), rollRadians: FixedQ4816.Zero, yawRadians: FixedQ4816.Zero);
        other.ApplyIntegrationResidue(residue: other.CaptureIntegrationResidue() with {
            RigidAngularVelocity = FixedVector3.Zero,
            RigidResting = true,
            RigidRestingHoldTicks = 1UL,
            RigidVelocity = FixedVector3.Zero,
        });

        var ballBefore = (ball.FixedPosition, ball.FixedOrientation);
        var overlappedBefore = Overlaps(left: ball, right: other);

        fixture.Step();

        return (ballBefore, (ball.FixedPosition, ball.FixedOrientation), (carrier.FixedPosition, carrier.FixedOrientation), ball.SweepRefusal, overlappedBefore, Overlaps(left: ball, right: other));
    }
    private static bool Overlaps(WorldBody left, WorldBody right) => FixedDynamicBodyContacts.TryCorrection(
        correction: out _,
        leftOrientation: left.FixedOrientation,
        leftPosition: left.FixedPosition,
        leftVolumes: left.ScaledColliderVolumes(),
        rightOrientation: right.FixedOrientation,
        rightPosition: right.FixedPosition,
        rightVolumes: right.ScaledColliderVolumes(),
        tieBreaker: 0
    );
    // The body's whole captured state, described member by member through every nested record and array, so two
    // captures compare by value and a member added later is described without a change here.
    private static string WholeState(WorldBody body) => string.Join(
        separator: Environment.NewLine,
        values: [
            $"residue {Describe(value: body.CaptureIntegrationResidue())}",
            $"transfer {Describe(value: body.CaptureTransferState())}",
            $"pose {body.FixedPosition} {body.FixedOrientation} {body.FixedYaw}",
            $"up {body.FixedUp}",
        ]
    );
    private static string Describe(object? value) {
        switch (value) {
            case null:
                return "null";
            case string text:
                return text;
            case System.Collections.IEnumerable sequence:
                return $"[{string.Join(separator: ", ", values: sequence.Cast<object?>().Select(selector: Describe))}]";
        }

        var type = value.GetType();

        // An InlineArray exposes its elements to no property, so they are read as a span of its element type.
        if (type.GetCustomAttributes(attributeType: typeof(System.Runtime.CompilerServices.InlineArrayAttribute), inherit: false) is [System.Runtime.CompilerServices.InlineArrayAttribute inline]) {
            var element = type.GetFields(bindingAttr: System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)[0].FieldType;
            var elements = typeof(BodySweepRefusalLawTests).GetMethod(bindingAttr: System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static, name: nameof(InlineElements))!
                .MakeGenericMethod(typeArguments: [type, element])
                .Invoke(obj: null, parameters: [value, inline.Length]);

            return Describe(value: elements);
        }

        if (
            type.IsPrimitive ||
            type.IsEnum ||
            (type.Namespace?.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck.World") != true)
        ) {
            return (Convert.ToString(provider: System.Globalization.CultureInfo.InvariantCulture, value: value) ?? string.Empty);
        }

        var properties = type.GetProperties(bindingAttr: System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(predicate: static property => (property.GetIndexParameters().Length == 0))
            .Select(selector: property => (property.Name, Value: property.GetValue(obj: value)));
        var fields = type.GetFields(bindingAttr: System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(selector: field => (field.Name, Value: field.GetValue(obj: value)));
        var members = properties.Concat(second: fields)
            .OrderBy(keySelector: static member => member.Name, comparer: StringComparer.Ordinal)
            .Select(selector: member => $"{member.Name}={Describe(value: member.Value)}");

        return $"{type.Name} {{ {string.Join(separator: ", ", values: members)} }}";
    }
    private static TElement[] InlineElements<TArray, TElement>(TArray value, int length) where TArray : struct =>
        System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(length: length, reference: ref System.Runtime.CompilerServices.Unsafe.As<TArray, TElement>(source: ref value)).ToArray();
    // A carrier walking forward ten ticks beside the wall program, with the ball posed at the given x and carried or not.
    private static (FixedVector3 Carrier, ContactRefusal BallRefusal, FixedQ4816 BallX) CarrierWalk(FixedQ4816 ballX, bool carrying) {
        using var fixture = Fixtures.FreshServer(definition: CarryFixtures.WallCarryDocument(includeWall: true));
        var carrier = fixture.JoinSeat();
        var ball = fixture.Server.Body(index: CarryFixtures.BallIndex)!;

        if (carrying) {
            Assert.True(condition: fixture.Server.Population.TryBeginCarry(carrierIndex: CarryFixtures.CarrierIndex, reason: out var reason, targetIndex: CarryFixtures.BallIndex), userMessage: reason);
        }

        ball.Pose(pitchRadians: FixedQ4816.Zero, position: new FixedVector3(X: ballX, Y: FixedQ4816.One, Z: FixedQ4816.Zero), rollRadians: FixedQ4816.Zero, yawRadians: FixedQ4816.Zero);
        carrier.EnqueueRun(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One), seconds: 1f);

        for (var tick = 0; (tick < 10); tick++) {
            fixture.Step();
        }

        return (carrier.FixedPosition, ball.SweepRefusal, ball.FixedPosition.X);
    }
}
