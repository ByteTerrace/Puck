using Puck.Commands;
using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for the rigid-dynamics facet (<see cref="WorldRigid"/>): pair-contact momentum conservation and
/// checkpoint/restore bit-exactness across a rigid body's own state.</summary>
public sealed class WorldRigidDynamicsLawTests {
    [Fact]
    public void FrozenNeighboursDoNotConsumeTheCandidateBudgetBeforeAnAwakeBody() {
        var source = LensOnFloorDocument();
        var definition = source with {
            CollisionRaw = source.Collision with {
                BodyContactsRaw = source.Collision.BodyContacts with {
                    CandidateBudget = 1,
                    MaxPairsPerBody = 1,
                    RigidPairIterationCeiling = 1,
                },
            },
        };
        using var fixture = TwoJoinedSeats(definition: definition);
        var principal = Principal.Seat(slot: 2);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(IdentityName: null, Principal: principal, Slot: 2, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        var first = fixture.Server.Body(index: 0)!;
        var second = fixture.Server.Body(index: 1)!;
        var arriving = fixture.Server.Body(index: 2)!;

        first.Pose(pitchRadians: 0, rollRadians: 0, x: 0, y: 0.13f, yawRadians: 0, z: 0);
        second.Pose(pitchRadians: 0, rollRadians: 0, x: 0.06f, y: 0.13f, yawRadians: 0, z: 0.06f);
        arriving.Pose(pitchRadians: 0, rollRadians: 0, x: 2, y: 0.13f, yawRadians: 0, z: 0);
        for (var tick = 0; ((tick < 600) && (fixture.Server.Population.SleepingRigidBodies != 3)); tick++) { fixture.Step(); }
        Assert.Equal(expected: 3, actual: fixture.Server.Population.SleepingRigidBodies);
        // The two frozen lenses' enclosing bounds overlap, but their actual volumes do not. In X sweep order
        // they both precede the arriving lens. Their unchanged pair must not consume first's one candidate.
        arriving.Pose(x: 0.065f, y: first.Position.Y, z: 0.03f, yawRadians: 0, pitchRadians: 0, rollRadians: 0);
        fixture.Step();
        Assert.False(condition: first.Resting);
    }
    [Fact]
    public void ACheckpointRetainsAPendingFieldWakeForASleepingRigidIsland() {
        using var fixture = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var body = fixture.JoinSeat();

        for (var tick = 0; ((tick < 600) && !body.Resting); tick++) { fixture.Step(); }
        Assert.True(condition: body.Resting);
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            checkpoint: out var captured, reason: out var reason), userMessage: reason);
        var entry = Assert.Single(collection: captured!.Population.Entries);

        Assert.True(condition: entry.Residue.ContactFieldObservationCurrent);
        captured = captured with {
            Population = captured.Population with {
                Entries = [entry with { Residue = entry.Residue with { ContactFieldObservationCurrent = false } }],
            },
        };
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured),
            checkpoint: out var decoded, reason: out reason), userMessage: reason);
        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        Assert.Equal(expected: 1, actual: fixture.Server.Population.SleepingRigidBodies);
        fixture.Step();
        Assert.False(condition: fixture.Server.Body(index: 0)!.Resting);
        Assert.Equal(expected: 0, actual: fixture.Server.Population.SleepingRigidBodies);
    }
    [Fact]
    public void SleepingRigidPopulationLeavesTickWorkAndWakesOnAnArrivingBody() {
        var source = FallingRigidBallDocument();
        var document = source with {
            PopulationRaw = source.Population with { CapacityRaw = 6, NetworkPlayers = 2 },
            PlacementRowsRaw = [.. source.Placements.Select(selector: row => row with { Region = new WorldPlacementRegion(Radius: 10) })],
        };
        using var fixture = Fixtures.FreshServer(definition: document);
        var population = fixture.Server.Population;

        Assert.Equal(expected: 2, actual: population.SetSimulatedCount(count: 2));
        var a = population.LocalSeatCount;
        var b = (a + 1);

        fixture.Server.Body(index: a)!.Pose(pitchRadians: 0, rollRadians: 0, x: -1, y: 0.5f, yawRadians: 0, z: 0);
        fixture.Server.Body(index: b)!.Pose(pitchRadians: 0, rollRadians: 0, x: 1, y: 0.5f, yawRadians: 0, z: 0);
        for (var tick = 0; ((tick < 600) && (population.SleepingRigidBodies != 2)); tick++) { fixture.Step(); }
        Assert.Equal(expected: 2, actual: population.SleepingRigidBodies);
        fixture.Step(); // Consume the last movement's event and snapshot work.
        for (var tick = 0; (tick < 10); tick++) {
            fixture.Step();
            Assert.Equal(expected: 0, actual: population.SimulatedBodyVisits);
            Assert.Equal(expected: 0, actual: population.DynamicContactPairVisits);
            Assert.Equal(expected: 0, actual: fixture.Server.Events.CollisionOverlapTests);
            Assert.Equal(expected: 0, actual: fixture.Server.Events.RegionBodyTests);
        }
        var sleeping = fixture.Server.Body(index: a)!;
        var arriving = fixture.Server.Body(index: b)!;
        var feed = new WorldEventFeed(capacity: population.Capacity);

        feed.Collect(definition: document, population: population);
        var regionId = document.Placements[0].Id;

        Assert.True(condition: feed.IsOccupant(body: a, placementId: regionId));
        var withoutRegion = document with {
            PlacementRowsRaw = [.. document.Placements.Select(selector: row => row with { Region = null })],
        };

        feed.Collect(definition: withoutRegion, population: population);
        sleeping.Glide(position: sleeping.FixedPosition with { X = FixedQ4816.FromInteger(value: 11) });
        for (var tick = 0; ((tick < 300) && (population.SleepingRigidBodies != 2)); tick++) { fixture.Step(); }
        Assert.Equal(expected: 2, actual: population.SleepingRigidBodies);
        // Re-adding an unchanged region after movement while it was absent must reconsider frozen occupants.
        feed.Collect(definition: document, population: population);
        Assert.False(condition: feed.IsOccupant(body: a, placementId: regionId));
        var before = sleeping.FixedPosition;

        arriving.Glide(position: (before + (FixedVector3.UnitX * FixedQ4816.FromDouble(value: 0.5))));
        fixture.Step();
        Assert.False(condition: sleeping.Resting);
        Assert.NotEqual(expected: before, actual: sleeping.FixedPosition);
        Assert.True(condition: (population.DynamicContactResolvedPairs > 0));
    }

    private static WorldDefinition LensOnFloorDocument() {
        var source = FallingRigidBallDocument();
        var creation = CreationCanonicalizer.Canonicalize(document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema, Name: "lens", Palette: null, Frames: null,
            Shapes: [new ShapeDocument(Id: 0, Name: "lens", Type: SdfSolidPrimitive.Ellipsoid,
                Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: new Vector3(x: 0.036f, y: 0.010f, z: 0.036f),
                Material: 0, Blend: SdfBlendOp.Union, Smooth: 0, Group: 0)]), source: "lens");

        return source with {
            CreationsRaw = [.. source.Creations, new WorldPrototype(Id: "lens", Document: creation.Document, HashRaw: creation.Hash)],
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                Collider = new WorldCollider.FromCreation(PrototypeId: "lens"),
                Rigid = kit.Rigid! with { Mass = 0.05f, Restitution = 0.05f, Friction = 0.6f, RollingFriction = 0.2f },
            })],
        };
    }

    [Fact]
    public void ATiltedLensTipsBeforeItRests() {
        using var fixture = Fixtures.FreshServer(definition: LensOnFloorDocument());
        var lens = fixture.JoinSeat();

        lens.Pose(pitchRadians: 0.5f, rollRadians: 0, x: 0, y: 0.12f, yawRadians: 0, z: 0);
        var initial = lens.FixedOrientation;

        for (var tick = 0; ((tick < 2400) && !lens.Resting); tick++) { fixture.Step(); }
        Assert.True(condition: lens.Resting, userMessage: $"position={lens.FixedPosition} velocity={lens.RigidVelocity} angular={lens.RigidAngularVelocity}");
        Assert.NotEqual(initial, lens.FixedOrientation);
        Assert.True(condition: (lens.FixedPosition.Y < FixedQ4816.FromDouble(value: 0.135)));
    }
    [Fact]
    public void AStackedLensRestsAndFallsWhenItsSupportMovesAway() {
        using var fixture = TwoJoinedSeats(definition: LensOnFloorDocument());
        var lower = fixture.Server.Body(index: 0)!;
        var upper = fixture.Server.Body(index: 1)!;

        upper.Pose(pitchRadians: 0, rollRadians: 0, x: 2, y: 0.20f, yawRadians: 0, z: 0);
        lower.Pose(pitchRadians: 0, rollRadians: 0, x: 0, y: 0.13f, yawRadians: 0, z: 0);
        for (var tick = 0; (tick < 150); tick++) { fixture.Step(); }
        upper.Pose(pitchRadians: 0, rollRadians: 0, x: 0, y: 0.20f, yawRadians: 0, z: 0);
        for (var tick = 0; ((tick < 2400) && !(lower.Resting && upper.Resting)); tick++) { fixture.Step(); }
        Assert.True(condition: (lower.Resting && upper.Resting), userMessage: $"lower={lower.FixedPosition} v={lower.RigidVelocity} hold={lower.RigidRestingHoldTicks}; upper={upper.FixedPosition} v={upper.RigidVelocity} hold={upper.RigidRestingHoldTicks}");
        Assert.True(condition: (upper.FixedPosition.Y > (lower.FixedPosition.Y + FixedQ4816.FromDouble(value: 0.015))), userMessage: $"lower={lower.FixedPosition}; upper={upper.FixedPosition}");
        for (var tick = 0; (tick < 30); tick++) {
            fixture.Step();
            Assert.True(condition: (lower.Resting && upper.Resting), userMessage: $"stack woke at {tick}: lower v={lower.RigidVelocity}, upper v={upper.RigidVelocity}");
            Assert.True(condition: (upper.FixedPosition.Y > (lower.FixedPosition.Y + FixedQ4816.FromDouble(value: 0.015))));
        }
        var supportedHeight = upper.FixedPosition.Y;

        Assert.Equal(expected: 2, actual: fixture.Server.Population.SleepingRigidBodies);

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            checkpoint: out var capturedStack, reason: out var captureReason), userMessage: captureReason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: capturedStack!),
            checkpoint: out var decodedStack, reason: out var decodeReason), userMessage: decodeReason);
        fixture.Server.RestoreCheckpoint(checkpoint: decodedStack!);
        lower = fixture.Server.Body(index: 0)!;
        upper = fixture.Server.Body(index: 1)!;
        Assert.Equal(expected: 2, actual: fixture.Server.Population.SleepingRigidBodies);
        fixture.Step();
        Assert.True(condition: (lower.Resting && upper.Resting));
        Assert.Equal(expected: supportedHeight, actual: upper.FixedPosition.Y);

        lower.Glide(position: (lower.FixedPosition + FixedVector3.UnitX));
        Assert.False(condition: upper.Resting, userMessage: "moving the support must wake its entire sleeping contact island");
        for (var tick = 0; (tick < 120); tick++) { fixture.Step(); }
        Assert.True(condition: (upper.FixedPosition.Y < (supportedHeight - FixedQ4816.FromDouble(value: 0.005))), userMessage: $"supported={supportedHeight}; upper={upper.FixedPosition}; sleeping={fixture.Server.Population.SleepingRigidBodies}");
    }
    [InlineData("glide")]
    [InlineData("reconcile")]
    [InlineData("scale")]
    [InlineData("collider")]
    [InlineData("program")]
    [Theory]
    public void GeometryChangesInvalidateRigidRest(string mutation) {
        var definition = Fixtures.WithBodyScale(initial: FixedQ4816.One, source: FallingRigidBallDocument());
        using var fixture = Fixtures.FreshServer(definition: definition);
        var body = fixture.JoinSeat();

        body.Pose(pitchRadians: ((mutation == "program") ? 0.5f : 0), rollRadians: 0, x: 0, y: 0.5f, yawRadians: 0, z: 0);
        for (var tick = 0; ((tick < 2400) && !body.Resting); tick++) { fixture.Step(); }
        Assert.True(condition: body.Resting);
        switch (mutation) {
            case "glide": body.Glide(position: (body.FixedPosition + FixedVector3.UnitY)); break;
            case "reconcile": body.Reconcile(seconds: 0, x: 1, yawRadians: 0, z: 0); break;
            case "scale":
                Fixtures.WriteBodyScale(body: 0, server: fixture.Server, value: FixedQ4816.FromDouble(value: 0.5));
                fixture.Step();
                break;
            case "collider":
            case "program":
                var program = CompiledBodyMotionProgram.Compile(kind: BodyProgramKind.Motion, name: "rest-law",
                    operations: ((mutation == "program") ? [BodyMotionOp.ResolveYawAttitudeAndPlanarFrame, BodyMotionOp.CommitPose] : [BodyMotionOp.CommitPose]), version: CompiledBodyMotionProgram.SupportedVersion);
                var collider = FixedWorldCollider.Compile(collider: new WorldCollider.Sphere(Radius: ((mutation == "collider") ? 0.2f : 0.4f)), creations: []);
                body.RecompileKit(tuning: default(FixedMotionTuning) with { Shaping = [], ShapingRecencyFacts = [], ShapingRecencyWindows = [] },
                    actions: null, actionThresholds: null, actionShapes: null, roleMask: null, roleOrdinals: default, actionState: null,
                    program: program, programs: new Dictionary<string, CompiledBodyMotionProgram> { [program.Name] = program },
                    collider: collider, maxSmoothError: FixedQ4816.Zero,
                    rigid: FixedWorldRigid.Compile(rigid: definition.Kits[0].Rigid!, collider: collider!.Value));
                break;
        }
        Assert.False(condition: body.Resting);
        Assert.Equal(0UL, body.RigidRestingHoldTicks);
        fixture.Step();
        Assert.False(condition: body.Resting);
    }
    [InlineData(0.05f)]
    [InlineData(0.06f)]
    [InlineData(0.15f)]
    [Theory]
    public void ASphereSpawnedAtOrAboveItsFloorCannotFallThrough(float height) {
        using var fixture = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        var ball = fixture.Server.Body(index: 0)!;

        Assert.True(condition: ball.IsRigid);
        ball.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: height, yawRadians: 0f, z: 0f);
        for (var tick = 0; (tick < 180); tick++) {
            fixture.Step();
            Assert.True(condition: (ball.FixedPosition.Y >= FixedQ4816.FromDouble(value: 0.049d)),
                userMessage: $"Spawn {height}, tick {tick}: position {ball.FixedPosition}, velocity {ball.RigidVelocity}, contact {ball.RigidGroundContacting}");
        }
        Assert.True(condition: (ball.RigidGroundContacting || ball.Resting));
        Assert.True(condition: ball.Grounded);
        Assert.True(condition: ((ball.Facts & BodyFacts.Grounded) != 0));
    }
    [Fact]
    public void AFallingRigidSphereReportsAirborneAndFalling() {
        using var fixture = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        var ball = fixture.Server.Body(index: 0)!;

        Assert.True(condition: ball.IsRigid);
        ball.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: 2f, yawRadians: 0f, z: 0f);
        fixture.Step();
        Assert.True(condition: (ball.RigidVelocity.Y < FixedQ4816.Zero));
        Assert.False(condition: ball.Grounded);
        Assert.True(condition: ((ball.Facts & BodyFacts.Airborne) != 0));
        Assert.True(condition: ((ball.Facts & BodyFacts.Falling) != 0));
    }
    [Fact]
    public void RestingSphereReleasesAndReacquiresItsGroundedFactAfterAnImpulse() {
        using var fixture = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        var ball = fixture.Server.Body(index: 0)!;

        ball.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: FloorTopY, yawRadians: 0f, z: 0f);
        for (var tick = 0; ((tick < 600) && !ball.Resting); tick++) {
            fixture.Step();
        }
        Assert.True(condition: ball.Resting);
        Assert.True(condition: ball.Grounded);

        Assert.True(condition: ball.TryApplyRigidImpulse(
            impulse: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: 3), Z: FixedQ4816.Zero),
            velocityCeiling: FixedQ4816.FromInteger(value: 100)));
        fixture.Step();
        Assert.False(condition: ball.Resting);
        Assert.False(condition: ball.Grounded);
        Assert.True(condition: ((ball.Facts & BodyFacts.Airborne) != 0));
        Assert.True(condition: ((ball.Facts & BodyFacts.Rising) != 0));

        var fellWhileAirborne = false;

        for (var tick = 0; ((tick < 600) && !ball.Resting); tick++) {
            fixture.Step();
            fellWhileAirborne |= (!ball.Grounded && ((ball.Facts & BodyFacts.Falling) != 0));
        }
        Assert.True(condition: fellWhileAirborne);
        Assert.True(condition: ball.Resting);
        Assert.True(condition: ball.Grounded);
        Assert.True(condition: ((ball.Facts & BodyFacts.Grounded) != 0));
        Assert.True(condition: ((ball.Facts & (BodyFacts.Airborne | BodyFacts.Rising | BodyFacts.Falling)) == 0));
    }

    private static WorldDefinition BoxOnFloorDocument() {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(
            Id: 0,
            Name: "floor",
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(
                x: 24f,
                y: (FloorTopY * 2f),
                z: 24f
            ),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "rigid-box-floor",
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: "rigid-box-floor"
        );
        var creation = new WorldPrototype(
            Id: "floor",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
        // Wood-plausible coefficients — no damping above what the brief refuses to add artificially; the manifold
        // itself, not authored drag, is what has to keep an upright body up.
        var rigid = new WorldRigid(
            AngularDamping: 0.02f,
            Friction: 0.5f,
            LinearDamping: 0.02f,
            Mass: 1f,
            Restitution: 0.1f,
            RollingFriction: 0.05f
        );

        return source with {
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
            CreationsRaw = [creation],
            GravityRaw = source.Gravity with {
                Uniform = new DocumentVector3(value: new Vector3(
            x: 0f,
            y: -9.8f,
            z: 0f
        )),
            },
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.Box(
                HalfExtents: new DocumentVector3(value: BoxHalfExtents),
                Rotation: Quaternion.Identity
            ),
                Rigid = rigid,
            })],
            PlacementRowsRaw = [new WorldPlacement(
                Id: "floor",
                PrototypeId: creation.Id,
                Position: Vector3.Zero,
                YawDegrees: 0f,
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f)
            )],
        };
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
            Scale: new Vector3(
                x: 24f,
                y: 0.1f,
                z: 24f
            ),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "rigid-floor",
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: "rigid-floor"
        );
        var creation = new WorldPrototype(
            Id: "floor",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
        var rigid = new WorldRigid(
            AngularDamping: 0.02f,
            Friction: 0.3f,
            LinearDamping: 0.01f,
            Mass: 1f,
            Restitution: 0.4f,
            RollingFriction: 0.05f
        );

        return source with {
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
            CreationsRaw = [creation],
            GravityRaw = source.Gravity with {
                Uniform = new DocumentVector3(value: new Vector3(
            x: 0f,
            y: -9.8f,
            z: 0f
        )),
            },
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.Sphere(Radius: 0.4f),
                Rigid = rigid,
            })],
            PlacementRowsRaw = [new WorldPlacement(
                Id: "floor",
                PrototypeId: creation.Id,
                Position: Vector3.Zero,
                YawDegrees: 0f,
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f)
            )],
        };
    }
    private static WorldFixture TwoJoinedSeats(WorldDefinition definition) {
        var fixture = Fixtures.FreshServer(definition: definition);
        var left = Principal.Seat(slot: 0);
        var right = Principal.Seat(slot: 1);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            left,
            left.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            right,
            right.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);

        return fixture;
    }
    /// <summary>An open-space, two-seat rigid-ball document: no placements, no gravity, no field requirement — a
    /// clean momentum-conservation arena where the only contact either seat can ever resolve is against the other.</summary>
    private static WorldDefinition TwoRigidBallsDocument(float mass, float restitution, float friction) {
        var source = Fixtures.BuildDocument();
        var rigid = new WorldRigid(
            AngularDamping: 0f,
            Friction: friction,
            LinearDamping: 0f,
            Mass: mass,
            Restitution: restitution,
            RollingFriction: 0f
        );

        return source with {
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.Sphere(Radius: 0.5f),
                Rigid = rigid,
            })],
        };
    }

    /// <summary>A body the rest latch has frozen (<c>WorldBody.AdvanceRigid</c>'s fast path) must WAKE when a live
    /// solid edit replaces the static world it rested against: lower the floor placement under a settled box and the
    /// box falls to the new floor rather than hanging frozen in the air with <c>resting=1</c>. The discriminating
    /// case for <c>WorldBody.SetContactField</c>'s wake — without it the freeze holds the box bit-identical at its
    /// old height forever, since nothing else ever touches it.</summary>
    [Fact]
    public void ARestingBoxWakesAndFallsWhenALiveSolidEditLowersItsFloor() {
        using var fixture = Fixtures.FreshServer(definition: BoxOnFloorDocument());
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            seat,
            seat.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);

        var box = fixture.Server.Body(index: 0)!;
        var baseRestY = (FloorTopY + BoxHalfExtents.Y);

        // Dropped from just above its rest height so the latch closes from a genuine settle, never a bare pose.
        box.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 0f,
            y: (baseRestY + 0.1f),
            yawRadians: 0f,
            z: 0f
        );

        for (var tick = 0; ((tick < 2_400) && !box.Resting); tick++) {
            fixture.Step();
        }

        Assert.True(
            condition: box.Resting,
            userMessage: $"the box never settled on the floor (v={box.RigidVelocity}, y={((double)box.FixedPosition.Y):0.###})"
        );

        var restY = box.FixedPosition.Y;
        var floor = WorldDefinitionRows.FindPlacement(
            id: "floor",
            placements: fixture.Server.Definition.Placements
        )!;
        const float Drop = 5f;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertPlacement(
            Placement: (floor with {
                Position = (floor.Position - new Vector3(
                x: 0f,
                y: Drop,
                z: 0f
            )),
            }),
            Principal: Principal.Console
        ));
        fixture.Step();

        var lowered = WorldDefinitionRows.FindPlacement(
            id: "floor",
            placements: fixture.Server.Definition.Placements
        )!;

        Assert.Equal(
            expected: (floor.Position.Y - Drop),
            actual: lowered.Position.Y
        );

        for (var tick = 0; (tick < 30); tick++) {
            fixture.Step();
        }

        Assert.True(
            condition: (box.FixedPosition.Y < (restY - FixedQ4816.FromDouble(value: 1d))),
            userMessage: $"the box stayed frozen at its old rest height after the floor dropped away (y={((double)box.FixedPosition.Y):0.###}, rest={((double)restY):0.###}, resting={box.Resting})"
        );
    }
    [Fact]
    public void BoxPastItsCriticalAngleTopplesAndControlUprightBoxStaysStanding() {
        using var fixture = Fixtures.FreshServer(definition: BoxOnFloorDocument());
        var left = Principal.Seat(slot: 0);
        var right = Principal.Seat(slot: 1);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            left,
            left.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            right,
            right.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);

        var tipped = fixture.Server.Body(index: 0)!;
        var control = fixture.Server.Body(index: 1)!;
        // Resting on the BASE face (Y half-extent vertical) sits the centre this high above the floor's own top.
        var baseRestY = (FloorTopY + BoxHalfExtents.Y);
        // 45° clears the box's own critical angle (atan(0.2 / 0.4) ≈ 26.6°): a real manifold lets gravity's torque
        // about the one corner still touching carry it the rest of the way over.
        var tippedRoll = (45f * (MathF.PI / 180f));

        tipped.Pose(
            pitchRadians: 0f,
            rollRadians: tippedRoll,
            x: -1.5f,
            y: baseRestY,
            yawRadians: 0f,
            z: 0f
        );
        control.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 1.5f,
            y: baseRestY,
            yawRadians: 0f,
            z: 0f
        );

        // A hard pose already clears the rest latch (WorldBody.Pose), so both boxes re-enter the solver on their
        // own; the negligible vertical wake is kept as the explicit, impulse-shaped "this body is live" statement
        // the law reads best with, adding no energy that matters next to gravity's own pull over the run below.
        var wake = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: 0.01d),
            Z: FixedQ4816.Zero
        );

        Assert.True(condition: tipped.TryApplyRigidImpulse(
            impulse: wake,
            velocityCeiling: FixedQ4816.FromDouble(value: 1_000d)
        ));
        Assert.True(condition: control.TryApplyRigidImpulse(
            impulse: wake,
            velocityCeiling: FixedQ4816.FromDouble(value: 1_000d)
        ));

        for (var tick = 0; (tick < 2_400); tick++) {
            fixture.Step();
        }

        // The box's own local up axis, rotated into world axes: 1 upright, 0 lying on a side face.
        static float UpAlignment(WorldBody body) => Vector3.Dot(
            vector1: Vector3.Transform(
                value: Vector3.UnitY,
                rotation: body.Orientation
            ),
            vector2: Vector3.UnitY
        );

        var tippedUpAlignment = UpAlignment(body: tipped);
        var controlUpAlignment = UpAlignment(body: control);

        // Finding-1's own defect: a manifold that never applies an impulse leaves a tilted box frozen at its posed
        // angle forever, so this alone already distinguishes the law from the bug it replaces — the box must have
        // moved measurably off its posed 45°.
        Assert.True(
            condition: (MathF.Abs(x: (tippedUpAlignment - MathF.Cos(x: tippedRoll))) > 0.1f),
            userMessage: $"the tipped box never moved off its posed 45° (upAlignment={tippedUpAlignment:0.###}, posed={MathF.Cos(x: tippedRoll):0.###}) — a manifold applying no impulse leaves it frozen exactly here"
        );
        // Past-critical: it topples onto a side face rather than climbing back upright.
        Assert.True(
            condition: (tippedUpAlignment < 0.3f),
            userMessage: $"the box past its own critical angle settled upright instead of toppling (upAlignment={tippedUpAlignment:0.###})"
        );
        Assert.True(
            condition: (tipped.FixedPosition.Y < FixedQ4816.FromDouble(value: (baseRestY - 0.05d))),
            userMessage: $"the toppled box's centre never dropped to its side-face resting height (y={((double)tipped.FixedPosition.Y):0.###}, base height={baseRestY:0.###})"
        );

        // Control: posed already upright and stable, it stays there — the SAME manifold that topples the other box
        // does not spuriously topple a body that never crossed its critical angle.
        Assert.True(
            condition: (controlUpAlignment > 0.9f),
            userMessage: $"the control box, posed flat, tipped over on its own (upAlignment={controlUpAlignment:0.###})"
        );
        Assert.True(
            condition: (control.RigidVelocity.LengthSquared < FixedQ4816.FromDouble(value: 0.01d)),
            userMessage: $"the control box never settled (v={control.RigidVelocity})"
        );
        // The rest LATCH, not just a loose speed bound: a manifold that leaves a persistent phantom velocity below
        // this threshold (a fraction of one substep's own gravity increment) would pass the bound above forever
        // without ever closing the hold window — see Resting's own remarks.
        Assert.True(
            condition: control.Resting,
            userMessage: $"the control box's speed read low but never actually latched to rest (v={control.RigidVelocity}, holdTicks={control.RigidRestingHoldTicks})"
        );
    }
    [Fact]
    public void HeadOnEqualMassElasticCollisionConservesMomentumAndSwapsVelocity() {
        using var fixture = TwoJoinedSeats(definition: TwoRigidBallsDocument(
            friction: 0f,
            mass: 1f,
            restitution: 1f
        ));
        var mover = fixture.Server.Body(index: 0)!;
        var target = fixture.Server.Body(index: 1)!;

        mover.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: -2f,
            y: 0f,
            yawRadians: 0f,
            z: 0f
        );
        target.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 2f,
            y: 0f,
            yawRadians: 0f,
            z: 0f
        );

        var approachVelocity = FixedQ4816.FromDouble(value: 4d);

        mover.TryApplyRigidImpulse(
            impulse: new FixedVector3(
                X: (approachVelocity * mover.RigidMass),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            velocityCeiling: FixedQ4816.FromDouble(value: 1_000d)
        );

        var momentumBefore = ((mover.RigidMass * mover.RigidVelocity.X) + (target.RigidMass * target.RigidVelocity.X));

        // Enough steps (at the fixture's 240 Hz step rate) to cross the 3-unit gap (less the two half-radii) at
        // 4 units/second and let the pair resolution settle the exchange, never so many that damping/friction
        // (both zero here) could matter.
        for (var tick = 0; (tick < 50); tick++) {
            fixture.Step();
        }

        // RigidPairResolvedCount is a per-TICK census (WorldPopulation.ResolveDynamicContacts resets it every
        // call), not a running total — by now the strike is long over, so the collision's own proof is the target's
        // velocity itself: it started at rest.
        Assert.True(
            condition: (target.RigidVelocity.X > FixedQ4816.Zero),
            userMessage: $"the pair never resolved a contact — mover={mover.FixedPosition} v={mover.RigidVelocity} target={target.FixedPosition} v={target.RigidVelocity}"
        );

        var momentumAfter = ((mover.RigidMass * mover.RigidVelocity.X) + (target.RigidMass * target.RigidVelocity.X));
        var momentumDrift = FixedQ4816.Abs(value: (momentumAfter - momentumBefore));

        Assert.True(
            condition: (momentumDrift < FixedQ4816.FromDouble(value: 0.05d)),
            userMessage: $"momentum drifted from {((double)momentumBefore):0.####} to {((double)momentumAfter):0.####} (Δ={((double)momentumDrift):0.####})"
        );

        // An equal-mass, unit-restitution, frictionless HEAD-ON strike (the contact anchor is colinear with the
        // normal, so no torque arises) is a full velocity swap: the mover ends near rest, the target near the
        // mover's original speed.
        Assert.True(
            condition: (FixedQ4816.Abs(value: mover.RigidVelocity.X) < FixedQ4816.FromDouble(value: 0.3d)),
            userMessage: $"mover kept {((double)mover.RigidVelocity.X):0.####} m/s — expected it to hand its velocity off"
        );
        Assert.True(
            condition: (target.RigidVelocity.X > FixedQ4816.FromDouble(value: 3d)),
            userMessage: $"target only picked up {((double)target.RigidVelocity.X):0.####} m/s of a {((double)approachVelocity):0.####} m/s strike"
        );
    }
    [Fact]
    public void RestingRigidCheckpointCarriesTheLiveFreezeLatchAndContactEdge() {
        using var fixture = Fixtures.FreshServer(definition: BoxOnFloorDocument());
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            seat,
            seat.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);

        var box = fixture.Server.Body(index: 0)!;

        box.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 0f,
            y: (FloorTopY + BoxHalfExtents.Y),
            yawRadians: 0f,
            z: 0f
        );

        for (var tick = 0; ((tick < 2_400) && !box.Resting); tick++) {
            fixture.Step();
        }

        Assert.True(
            condition: box.Resting,
            userMessage: $"the box never reached the exact rest freeze (v={box.RigidVelocity}, holdTicks={box.RigidRestingHoldTicks})"
        );
        var frozenPosition = box.FixedPosition;

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                checkpoint: out var captured,
                reason: out var captureReason
            ),
            userMessage: captureReason
        );
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: bytes,
                checkpoint: out var decoded,
                reason: out var decodeReason
            ),
            userMessage: decodeReason
        );

        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        box = fixture.Server.Body(index: 0)!;
        var restored = box.CaptureIntegrationResidue();

        Assert.True(condition: restored.RigidResting);
        Assert.True(condition: (restored.RigidRestingHoldTicks > 0UL));
        Assert.True(condition: restored.RigidGroundContacting);
        Assert.Equal(
            expected: FixedVector3.Zero,
            actual: restored.RigidVelocity
        );
        Assert.Equal(
            expected: FixedVector3.Zero,
            actual: restored.RigidAngularVelocity
        );
        Assert.Equal(
            expected: frozenPosition,
            actual: box.FixedPosition
        );

        for (var tick = 0; (tick < 15); tick++) {
            fixture.Step();
        }

        Assert.True(condition: box.Resting);
        Assert.Equal(
            expected: frozenPosition,
            actual: box.FixedPosition
        );
    }
    [Fact]
    public void RigidBodyCheckpointResumesBitExactlyMidFallAndBounce() {
        using var fixture = Fixtures.FreshServer(definition: FallingRigidBallDocument());
        var left = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            left,
            left.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);

        var ball = fixture.Server.Body(index: 0)!;

        ball.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 0f,
            y: 3f,
            yawRadians: 0f,
            z: 0f
        );
        ball.TryApplyRigidImpulse(
            impulse: new FixedVector3(
                X: FixedQ4816.FromDouble(value: 2d),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.FromDouble(value: 0.7d)
            ),
            velocityCeiling: FixedQ4816.FromDouble(value: 1_000d)
        );

        for (var tick = 0; (tick < 12); tick++) {
            fixture.Step();
        }

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                checkpoint: out var captured,
                reason: out var captureReason
            ),
            userMessage: captureReason
        );
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: bytes,
                checkpoint: out var decoded,
                reason: out var decodeReason
            ),
            userMessage: decodeReason
        );

        for (var tick = 0; (tick < 38); tick++) {
            fixture.Step();
        }
        Assert.True(
            condition: (ball.RigidAngularVelocity.LengthSquared > FixedQ4816.Zero),
            userMessage: "the ball never picked up any spin — the residue this test exercises would be vacuous"
        );
        var expected = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 0UL
        );

        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        for (var tick = 0; (tick < 38); tick++) {
            fixture.Step();
        }

        Assert.Equal(
            expected: expected,
            actual: WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0UL
            )
        );
    }

    private const float FloorTopY = 0.05f;

    // Half-extents whose two ground-plane axes differ (0.2 and 0.4), so resting on the base face and resting on a
    // side face sit the centre of mass at two different, easily distinguished heights — a floor-relative box, one
    // seat per body, otherwise on the same terms as FallingRigidBallDocument.
    private static readonly Vector3 BoxHalfExtents = new(
        x: 0.2f,
        y: 0.4f,
        z: 0.2f
    );
}
