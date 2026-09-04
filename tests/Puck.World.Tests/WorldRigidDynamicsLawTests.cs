using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the rigid-dynamics facet (<see cref="WorldRigid"/>): pair-contact momentum conservation and
/// checkpoint/restore bit-exactness across a rigid body's own state.</summary>
public sealed class WorldRigidDynamicsLawTests {
    /// <summary>An open-space, two-seat rigid-ball document: no placements, no gravity, no field requirement — a
    /// clean momentum-conservation arena where the only contact either seat can ever resolve is against the other.</summary>
    private static WorldDefinition TwoRigidBallsDocument(float mass, float restitution, float friction) {
        var source = Fixtures.BuildDocument();
        var rigid = new WorldRigid(Mass: mass, Restitution: restitution, Friction: friction, RollingFriction: 0f, LinearDamping: 0f, AngularDamping: 0f);

        return source with {
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.Sphere(Radius: 0.5f),
                Rigid = rigid,
            })],
        };
    }
    private static WorldFixture TwoJoinedSeats(WorldDefinition definition) {
        var fixture = Fixtures.FreshServer(definition: definition);
        var left = WorldPrincipal.Seat(slot: 0);
        var right = WorldPrincipal.Seat(slot: 1);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(left, left.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(right, right.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        return fixture;
    }

    [Fact]
    public void HeadOnEqualMassElasticCollisionConservesMomentumAndSwapsVelocity() {
        using var fixture = TwoJoinedSeats(definition: TwoRigidBallsDocument(mass: 1f, restitution: 1f, friction: 0f));
        var mover = fixture.Server.Body(index: 0)!;
        var target = fixture.Server.Body(index: 1)!;

        mover.Pose(x: -2f, y: 0f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        target.Pose(x: 2f, y: 0f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        var approachVelocity = FixedQ4816.FromDouble(value: 4d);

        mover.TryApplyRigidImpulse(impulse: new FixedVector3(
            X: (approachVelocity * mover.RigidMass),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        ));

        var momentumBefore = ((mover.RigidMass * mover.RigidVelocity.X) + (target.RigidMass * target.RigidVelocity.X));

        // Enough steps (at the fixture's 240 Hz step rate) to cross the 3-unit gap (less the two half-radii) at
        // 4 units/second and let the pair resolution settle the exchange, never so many that damping/friction
        // (both zero here) could matter.
        for (var tick = 0; (tick < 400); tick++) {
            fixture.Step();
        }

        // RigidPairResolvedCount is a per-TICK census (WorldPopulation.ResolveDynamicContacts resets it every
        // call), not a running total — by now the strike is long over, so the collision's own proof is the target's
        // velocity itself: it started at rest.
        Assert.True(condition: (target.RigidVelocity.X > FixedQ4816.Zero), userMessage: $"the pair never resolved a contact — mover={mover.FixedPosition} v={mover.RigidVelocity} target={target.FixedPosition} v={target.RigidVelocity}");

        var momentumAfter = ((mover.RigidMass * mover.RigidVelocity.X) + (target.RigidMass * target.RigidVelocity.X));
        var momentumDrift = FixedQ4816.Abs(value: (momentumAfter - momentumBefore));

        Assert.True(condition: (momentumDrift < FixedQ4816.FromDouble(value: 0.05d)),
            userMessage: $"momentum drifted from {((double)momentumBefore):0.####} to {((double)momentumAfter):0.####} (Δ={((double)momentumDrift):0.####})");

        // An equal-mass, unit-restitution, frictionless HEAD-ON strike (the contact anchor is colinear with the
        // normal, so no torque arises) is a full velocity swap: the mover ends near rest, the target near the
        // mover's original speed.
        Assert.True(condition: (FixedQ4816.Abs(value: mover.RigidVelocity.X) < FixedQ4816.FromDouble(value: 0.3d)),
            userMessage: $"mover kept {((double)mover.RigidVelocity.X):0.####} m/s — expected it to hand its velocity off");
        Assert.True(condition: (target.RigidVelocity.X > FixedQ4816.FromDouble(value: 3d)),
            userMessage: $"target only picked up {((double)target.RigidVelocity.X):0.####} m/s of a {((double)approachVelocity):0.####} m/s strike");
    }

    /// <summary>A flat solid floor plus uniform downward gravity — a single rigid ball dropped, bounced, and rolling
    /// so its checkpoint residue (angular velocity, resting hold ticks, both restitution edge latches) is genuinely
    /// live, never zero by construction.</summary>
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
        var rigid = new WorldRigid(Mass: 1f, Restitution: 0.4f, Friction: 0.3f, RollingFriction: 0.05f, LinearDamping: 0.01f, AngularDamping: 0.02f);

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
    public void RigidBodyCheckpointResumesBitExactlyMidFallAndBounce() {
        using var fixture = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var left = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(left, left.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var ball = fixture.Server.Body(index: 0)!;

        ball.Pose(x: 0f, y: 3f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        ball.TryApplyRigidImpulse(impulse: new FixedVector3(
            X: FixedQ4816.FromDouble(value: 2d),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: 0.7d)
        ));

        for (var tick = 0; (tick < 100); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            hostRow: EmptyHostRow(),
            checkpoint: out var captured,
            reason: out var captureReason
        ), userMessage: captureReason);
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: bytes, checkpoint: out var decoded, reason: out var decodeReason), userMessage: decodeReason);

        for (var tick = 0; (tick < 300); tick++) {
            fixture.Step();
        }
        Assert.True(condition: (ball.RigidAngularVelocity.LengthSquared > FixedQ4816.Zero), userMessage: "the ball never picked up any spin — the residue this test exercises would be vacuous");
        var expected = WorldRuntimeStateHash.HashAuthoritative(server: fixture.Server, tick: 0UL);

        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        for (var tick = 0; (tick < 300); tick++) {
            fixture.Step();
        }

        Assert.Equal(expected: expected, actual: WorldRuntimeStateHash.HashAuthoritative(server: fixture.Server, tick: 0UL));
    }

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [], AppliedTransferHighWater: null, AppliedTransferIds: [], ElapsedEngineTicks: 0,
        ForwardedBodies: [], FreshCounter: 0, InDoubtTransfers: [], IsPaused: false, NextTransferId: 1,
        PortalOccupancy: [], Retained: false, ScheduleAccumulatorTicks: 0, SeededArrivals: []);
}
