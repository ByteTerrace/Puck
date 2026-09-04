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
