using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the <c>body.impulse</c>/<c>world.rigid</c> command surface: the <see cref="WorldCommand.RigidImpulse"/>
/// wire leaf, the server-side <c>IsRigid</c> refusal, and the <c>$physics:quiescent</c> rule fact
/// (<see cref="WorldPopulation.RigidBodiesQuiescent"/>).</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class WorldRigidCommandLawTests {
    [Fact]
    public void RigidImpulseWireLeafRoundTripsThroughSubmissionCodec() {
        var command = new WorldCommand.RigidImpulse(
            Principal: WorldPrincipal.Seat(slot: 2),
            EntityIndex: 7,
            Impulse: new Vector3(x: 1.5f, y: -2.25f, z: 0.125f)
        );

        Assert.True(condition: WorldSubmissionCodec.TryEncodeCommand(
            command: command,
            bytes: out var bytes,
            failure: out var encodeFailure
        ), userMessage: encodeFailure.ToString());
        Assert.True(condition: WorldSubmissionCodec.TryDecodeCommand(
            bytes: bytes,
            command: out var decoded,
            failure: out var decodeFailure
        ), userMessage: decodeFailure.ToString());

        var roundTripped = Assert.IsType<WorldCommand.RigidImpulse>(@object: decoded);

        Assert.Equal(expected: command.Principal, actual: roundTripped.Principal);
        Assert.Equal(expected: command.EntityIndex, actual: roundTripped.EntityIndex);
        Assert.Equal(expected: command.Impulse, actual: roundTripped.Impulse);
    }

    [Fact]
    public void RigidImpulseAgainstNonRigidBodyIsRefusedByNameAndLeavesVelocityUntouched() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument());
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: 0)!;

        Assert.False(condition: body.IsRigid, userMessage: "the fixture's default kit must stay a locomotion kit for this control to discriminate anything");

        var originalError = Console.Error;
        using var captured = new StringWriter();

        try {
            Console.SetError(newError: captured);

            fixture.Server.ApplyCommand(command: new WorldCommand.RigidImpulse(
                Principal: seat,
                EntityIndex: 0,
                Impulse: new Vector3(x: 5f, y: 0f, z: 0f)
            ));
        } finally {
            Console.SetError(newError: originalError);
        }

        Assert.Contains(
            actualString: captured.ToString(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "body:0 carries no rigid kit facet"
        );
        // A non-rigid body's RigidVelocity always reads Zero by construction (see WorldBody.Rigid.cs), so the real
        // proof the impulse never reached a solver is the refusal narration above; this is the un-refused CONTROL
        // side — a rigid body's velocity DOES move (WorldRigidDynamicsLawTests' own collision proof).
        Assert.Equal(expected: FixedVector3.Zero, actual: body.RigidVelocity);
    }

    // A flat solid floor plus uniform downward gravity, mirroring WorldRigidDynamicsLawTests' own falling-ball
    // fixture — quiescence needs actual ground contact to reach, never open space.
    private static WorldDefinition FallingRigidBallDocument() {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(
            Id: 0,
            Name: "floor",
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 24f, y: 0.1f, z: 24f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "rigid-floor",
            Palette: null,
            Shapes: [shape],
            Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "rigid-floor");
        var creation = new WorldPrototype(Id: "floor", Document: canonical.Document, HashRaw: canonical.Hash);
        var rigid = new WorldRigid(Mass: 1f, Restitution: 0.1f, Friction: 0.4f, RollingFriction: 0.2f, LinearDamping: 0.05f, AngularDamping: 0.05f);

        return source with {
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
            CreationsRaw = [creation],
            GravityRaw = source.Gravity with { Uniform = new DocumentVector3(value: new Vector3(x: 0f, y: -9.8f, z: 0f)) },
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.Sphere(Radius: 0.4f),
                Rigid = rigid,
            })],
            PlacementRowsRaw = [new WorldPlacement(Id: "floor", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
        };
    }

    // FallingRigidBallDocument plus an authored bodies.scaleRow naming a keyed kind=fixed "scale" row for body 0 —
    // the same shape BodyScaleLawTests.WithScaleRow builds, added onto the falling-ball fixture rather than the
    // bare-locomotion one so the scaled body actually has a rigid facet to advance.
    private static WorldDefinition ScaledFallingRigidBallDocument(FixedQ4816 cellValue) {
        var source = FallingRigidBallDocument();
        var scaleRow = new WorldStateRow(
            Name: CellName.Parse(candidate: "scale"),
            Kind: CellKind.Fixed,
            Min: FixedQ4816.FromDouble(value: 0.05).Value,
            Max: FixedQ4816.One.Value,
            Capacity: 8,
            Cells: [new WorldStateCell(Key: CellName.Parse(candidate: "0"), Value: cellValue.Value)]
        );

        return (source with {
            PopulationRaw = (source.Population with { ScaleRow = "scale" }),
            StateRaw = ((source.StateRaw ?? new WorldStateSection()) with {
                World = [.. (source.StateRaw?.World ?? []), scaleRow],
            }),
        });
    }

    [Fact]
    public void ScaleRigidMassAndBoundingRadiusFollowScaleCubedAndScaleAgainstTheScaleOneControl() {
        var authoredMass = FixedQ4816.One;
        var authoredRadius = FixedQ4816.FromDouble(value: 0.4);
        var half = FixedQ4816.FromDouble(value: 0.5);

        using var control = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: control.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var controlBall = control.Server.Body(index: 0)!;

        Assert.Equal(expected: authoredMass, actual: controlBall.RigidMass);
        Assert.Equal(expected: authoredRadius, actual: controlBall.RigidBoundingRadius);

        using var shrunk = Fixtures.FreshServer(definition: ScaledFallingRigidBallDocument(cellValue: half));

        Assert.True(condition: shrunk.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var shrunkBall = shrunk.Server.Body(index: 0)!;

        Assert.Equal(expected: half, actual: shrunkBall.Scale);
        // mass ∝ Scale³: 1 · 0.5³ = 0.125. Bounding radius ∝ Scale: 0.4 · 0.5 = 0.2.
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 0.125), actual: shrunkBall.RigidMass);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 0.2), actual: shrunkBall.RigidBoundingRadius);
    }

    // Scale 0.05 is the garden's own authored envelope floor (state.world 'scale' row's own min); its scale^5
    // rounds to a zero raw at Q16, the case ScaleRigid's reciprocal-of-scale construction must not divide by. A
    // shrunk body also takes more static-contact substeps per tick (its bounding radius bounds the substep travel),
    // so the miss-streak grace this proves against is the harder case, not merely the scale-one control
    // PhysicsQuiescentReadsFalseWhileFallingAndTrueOnceEveryRigidBodyRests already covers.
    [Fact]
    public void ScaledRigidBallAtTheAuthoredEnvelopeFloorFallsAndSettlesWithoutThrowing() {
        using var fixture = Fixtures.FreshServer(definition: ScaledFallingRigidBallDocument(cellValue: FixedQ4816.FromDouble(value: 0.05)));
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var ball = fixture.Server.Body(index: 0)!;

        ball.Pose(x: 0f, y: 3f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        for (var tick = 0; (tick < 4000); tick++) {
            fixture.Step();
        }

        Assert.True(condition: ball.Resting, userMessage: $"the scale-0.05 ball never settled — resting={ball.Resting} v={ball.RigidVelocity} pos={ball.FixedPosition}");
    }

    // A settled ball struck horizontally must never have its linear velocity cross back through zero into the
    // opposite sign — the artifact a starved InverseInertia (kept at the FULL-SIZE body's magnitude while
    // InverseMass already reflects the shrunk one) produces when friction keeps subtracting linear momentum with
    // nowhere for it to go. Scale 1 is the control (ScaleRigid's own identity fast path — this law's fixture never
    // reaches the overflow branch there, unlike the garden's own rollingFriction=1.2 configuration, which reverses
    // at EVERY scale including 1 for a strong enough strike — a separate, scale-independent friction/rolling
    // overshoot this law does not exercise); scale 0.2 sits past this fixture's own overflow threshold (~0.07,
    // below the garden billiard ball's ~0.135 — a smaller unscaled 1/I here) and additionally proves spin develops
    // post-fix; scale 0.05 (the garden's own authored floor) is deep enough past it that this fixture's mass —
    // cubed down to ~1e-4 at that scale — starves the friction impulse below FixedQ4816's representable floor
    // before InverseInertia ever multiplies it, so spin-generation is not asserted there; the reversal check is
    // what this law actually proves at that scale.
    [Theory]
    [InlineData(1d, true)]
    [InlineData(0.2d, true)]
    [InlineData(0.05d, false)]
    public void ScaledRigidBallInverseInertiaStaysConsistentWithScaledMassAcrossAStrike(double scale, bool expectSpin) {
        var scaleValue = FixedQ4816.FromDouble(value: scale);
        var definition = ((scaleValue == FixedQ4816.One)
            ? FallingRigidBallDocument()
            : ScaledFallingRigidBallDocument(cellValue: scaleValue)
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var ball = fixture.Server.Body(index: 0)!;

        ball.Pose(x: 0f, y: 3f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        for (var tick = 0; (tick < 4000); tick++) {
            fixture.Step();
        }

        Assert.True(condition: ball.Resting, userMessage: $"scale={scale}: ball never settled before the strike — resting={ball.Resting}");

        // Scaled by scale³ — mass ∝ Scale³, so an impulse scaled the SAME way keeps the resulting velocity
        // (impulse/mass) roughly comparable across scales, matching a cue strike rather than a body-length-per-tick
        // teleport that flies the ball off the ground before friction ever touches it.
        Assert.True(condition: ball.TryApplyRigidImpulse(
            impulse: new FixedVector3(
                X: (FixedQ4816.FromDouble(value: -0.85d) * scaleValue * scaleValue * scaleValue),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            velocityCeiling: FixedQ4816.FromDouble(value: 1_000d)
        ), userMessage: $"scale={scale}: the strike impulse was refused");

        var sawSpin = false;

        for (var tick = 0; (tick < 30); tick++) {
            fixture.Step();

            Assert.True(
                condition: (ball.RigidVelocity.X <= FixedQ4816.Zero),
                userMessage: $"scale={scale} tick={tick}: velocity reversed sign — v={ball.RigidVelocity}"
            );

            if (ball.RigidAngularVelocity.LengthSquared > FixedQ4816.Zero) {
                sawSpin = true;
            }
        }

        if (expectSpin) {
            Assert.True(condition: sawSpin, userMessage: $"scale={scale}: ball never developed angular velocity from a horizontal strike while grounded");
        }
    }

    [Fact]
    public void TryApplyRigidImpulseRefusesAResultingVelocityOverTheCeilingWithoutThrowingAndControlStillApplies() {
        using var fixture = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var ball = fixture.Server.Body(index: 0)!;
        var ceiling = FixedQ4816.FromDouble(value: 20d);

        Assert.False(condition: ball.TryApplyRigidImpulse(
            impulse: new FixedVector3(
                X: FixedQ4816.FromDouble(value: 1_000_000_000_000d),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            velocityCeiling: ceiling
        ), userMessage: "a degenerate impulse magnitude must be refused rather than applied");
        Assert.Equal(expected: FixedVector3.Zero, actual: ball.RigidVelocity);

        Assert.True(condition: ball.TryApplyRigidImpulse(
            impulse: new FixedVector3(
                X: FixedQ4816.FromDouble(value: 2d),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            velocityCeiling: ceiling
        ), userMessage: "an ordinary impulse well under the ceiling must still apply");
        Assert.NotEqual(expected: FixedVector3.Zero, actual: ball.RigidVelocity);

        for (var tick = 0; (tick < 60); tick++) {
            fixture.Step();
        }
    }

    [Fact]
    public void TryApplyRigidImpulseRefusesComponentAdditionOverflowWithoutChangingVelocity() {
        using var fixture = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var ball = fixture.Server.Body(index: 0)!;
        var nearMaximum = FixedQ4816.FromRawBits(value: (long.MaxValue - 32L));

        Assert.True(condition: ball.TryApplyRigidImpulse(
            impulse: new FixedVector3(X: nearMaximum, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            velocityCeiling: FixedQ4816.MaxValue
        ), userMessage: "the representable control impulse must establish the near-limit velocity");

        var before = ball.RigidVelocity;

        Assert.False(condition: ball.TryApplyRigidImpulse(
            impulse: new FixedVector3(X: FixedQ4816.FromRawBits(value: 64L), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            velocityCeiling: FixedQ4816.MaxValue
        ), userMessage: "adding a representable delta to a near-limit velocity must refuse instead of wrapping negative");
        Assert.Equal(expected: before, actual: ball.RigidVelocity);
    }

    [Fact]
    public void PhysicsQuiescentReadsFalseWhileFallingAndTrueOnceEveryRigidBodyRests() {
        using var fixture = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var ball = fixture.Server.Body(index: 0)!;

        ball.Pose(x: 0f, y: 3f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        // Mid-fall: not grounded, well above the resting-hold window — the fact must read false, never a vacuous
        // true a body-count-only implementation would report from tick 0.
        for (var tick = 0; ((tick < 20) && !fixture.Server.Population.RigidBodiesQuiescent()); tick++) {
            fixture.Step();
        }
        Assert.False(condition: fixture.Server.Population.RigidBodiesQuiescent(), userMessage: "the ball is still falling — quiescent must not read true mid-fall");

        // Long enough (at 240 Hz) to land, bleed its bounce through the authored damping/friction, and clear the
        // rest-hold window.
        for (var tick = 0; (tick < 3000); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.Population.RigidBodiesQuiescent(), userMessage: $"the ball never settled — resting={ball.Resting} v={ball.RigidVelocity} pos={ball.FixedPosition}");
    }
}
