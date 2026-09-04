using System.Numerics;
using Puck.World.Authoring;
using Puck.Hosting;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Law coverage for the ordered hold list a grounded kit authors: which row the resolve takes, the frame
/// it hands the planar target, the vertical law each kind applies, the spend, the published facts, and the
/// validator's refusals.</summary>
/// <remarks>The fixture runs on the SDF field contact provider (<c>SmoothUnionContact</c>), the one the shipped
/// world selects, and puts the floor and the wall in ONE holdable placement — the shape that makes an undirected
/// surface query answer with the floor a body is standing on rather than the wall in front of it, and that an
/// analytic-collider fixture cannot reproduce.</remarks>
public sealed class HoldLawTests {
    private const int ForwardOrdinal = 0;
    private const int ReleaseOrdinal = 3;
    private const int StrafeOrdinal = 1;
    private const int UpOrdinal = 4;
    // The room, in engine coordinates: a floor slab whose top face is y = 0, and a wall standing on it spanning
    // y 0..WallTop with its near face at z = WallFaceZ, square in front of a body at yaw 0 (which faces -Z).
    private const double WallFaceZ = -2.75;
    private const float WallTop = 2f;
    // The ceiling fixture: a slab whose underside sits CeilingY above the floor, reachable only from a body posed
    // under it and out of reach of the floor.
    private const float CeilingY = 6f;

    // The fixture's shared vertical arc and envelope: every Gravity/Lift row's own fields.
    private static readonly WorldHoldGravity DefaultGravity = new(Fall: 23f, Rise: 14f);
    private static readonly WorldHoldEnvelope DefaultEnvelope = new(SinkSpeed: 20f);

    private static WorldHold Air(BodyHoldKind kind = BodyHoldKind.Gravity, float lift = 0f, float thrust = 0f, WorldHoldGravity? gravity = null) => new(
        Bond: BodyHoldBond.Free,
        Envelope: ((kind is BodyHoldKind.Gravity || ((kind == BodyHoldKind.Lift) && (lift < 1f)))
        ? DefaultEnvelope
        : null),
        Gravity: ((kind is BodyHoldKind.Gravity or BodyHoldKind.Lift)
        ? (gravity ?? DefaultGravity)
        : null),
        Hold: kind,
        Lift: lift,
        Name: "air",
        Thrust: thrust
    );
    private static WorldHold Ground(float coneMax = 60f, WorldHoldGravity? gravity = null) => new(
        Bond: BodyHoldBond.Surface,
        Cone: new Vector2(x: 0f, y: coneMax),
        Envelope: DefaultEnvelope,
        Gravity: (gravity ?? DefaultGravity),
        Hold: BodyHoldKind.Gravity,
        Name: "ground",
        Reach: 1.2f
    );
    private static WorldHold Wall(bool onDrive = true, float speed = 2f, float upLean = 0f, string? release = "release", WorldHoldSpend? spend = null, float coneMin = 60f, float coneMax = 120f, float reach = 0.8f) => new(
        Bond: BodyHoldBond.Surface,
        Cone: new Vector2(x: coneMin, y: coneMax),
        DriveAlignment: 0.5f,
        Pull: 1f,
        Hold: BodyHoldKind.Pull,
        Name: "wall",
        OnDrive: onDrive,
        Reach: reach,
        Release: release,
        Speed: speed,
        Spend: spend,
        UpLean: upLean
    );
    private static ShapeDocument Box(int id, string name, Vector3 position, Vector3 halfExtents) => new(
        Id: id,
        Name: name,
        Type: SdfSolidPrimitive.Box,
        Position: position,
        Rotation: Quaternion.Identity,
        Scale: halfExtents,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );
    // Author frame; the engine reads it through the half-turn (-x, y, -z), so the wall authored at +z stands at
    // engine -z.
    private static WorldPrototype BuildRoom(bool ceiling) {
        var shapes = new List<ShapeDocument> {
            Box(
                halfExtents: new Vector3(x: 20f, y: 0.5f, z: 20f),
                id: 0,
                name: "floor",
                position: new Vector3(x: 0f, y: -0.5f, z: 0f)
            ),
            Box(
                halfExtents: new Vector3(x: 6f, y: (WallTop / 2f), z: 0.25f),
                id: 1,
                name: "wall",
                position: new Vector3(x: 0f, y: (WallTop / 2f), z: 3f)
            ),
        };

        if (ceiling) {
            shapes.Add(item: Box(
                halfExtents: new Vector3(x: 6f, y: 0.5f, z: 6f),
                id: 2,
                name: "ceiling",
                position: new Vector3(x: 0f, y: (CeilingY + 0.5f), z: -10f)
            ));
        }

        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "room",
                Palette: null,
                Shapes: shapes,
                Frames: null
            ),
            source: "room"
        );

        return new WorldPrototype(
            Id: "room",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    private static WorldDefinition BuildHoldDocument(IReadOnlyList<WorldHold>? holds, bool ceiling = false, float stamina = 0f, bool holdable = true) {
        var document = Fixtures.BuildDocument();
        var channels = document.Channels.ToList();

        channels.Add(item: new WorldChannel(Name: "release", Shape: ChannelShape.Binary, Composition: true));
        channels.Add(item: new WorldChannel(Name: "up", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveUp));

        var kits = document.Kits.ToList();
        var motion = kits[0].Motion!;

        kits[0] = (kits[0] with {
            Collider = new WorldCollider.Capsule(Endpoint: new Vector3(x: 0f, y: 1f, z: 0f), Radius: 0.35f),
            Motion = (motion with { Holds = holds }),
            BodyMotionProgram = "hold",
        });

        var programs = document.BodyMotionPrograms.ToList();

        programs.Add(item: new BodyMotionProgram(
            Name: "hold",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputePlanarTargetVelocity,
                BodyMotionOp.ShapeVelocity,
                BodyMotionOp.RunActionTriggers,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        ));

        return document with {
            BodyMotionProgramsRaw = programs,
            ChannelsRaw = channels,
            // The shipped world's own provider: the analytic collider set answers a surface query from a
            // nearest-point search over convex primitives, the field answers it from a ray march, and only the
            // second is what a real world runs.
            CollisionRaw = new WorldCollision(
                ContactSkin: 0.02f,
                DefaultHold: false,
                GradientProbe: 0f,
                MaxIterations: 4,
                MaxSlopeDegrees: 60f,
                Requirements: [WorldContactRequirement.SmoothUnionContact]
            ),
            CreationsRaw = [BuildRoom(ceiling: ceiling)],
            KitRowsRaw = kits,
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = [
                    new WorldPlacement(
                        Id: "room1",
                        PrototypeId: "room",
                        Position: Vector3.Zero,
                        YawDegrees: 0f,
                        Scale: 1f,
                        Solid: new WorldSolid(Margin: 0f),
                        Grip: (holdable
                        ? new WorldPlacementGrip(Holdable: true)
                        : null)
                    ),
                ],
            }),
            StateRaw = new WorldStateSection(
                Body: ((stamina > 0f)
                ? [new ActionStateSlot(Name: "stamina", Kind: ActionStateKind.Counter, Initial: stamina)]
                : null),
                World: []
            ),
        };
    }
    // One inhabitant placement: a wanderer of the fixture kit, standing on its own ground.
    private static WorldPlacement Den(string id, float x, float z) => new(
        Id: id,
        PrototypeId: "room",
        Position: new Vector3(x: x, y: 0f, z: z),
        YawDegrees: 0f,
        Scale: 1f,
        Inhabit: new WorldPlacementInhabit(
            Count: 1,
            Distribution: new WorldDistribution(
                Region: new WorldDistributionRegion.Disc(Radius: 0.01f, SampleCount: 1),
                Fill: new WorldSequence(Name: WorldSequence.Additive, Offset: 0, Step: 0.618034f)
            ),
            Kit: Fixtures.SeatKitName,
            Look: null,
            Source: IntentSource.Producer(name: "wander")
        )
    );
    private static PlayerIntent Ascend() => Channel(
        ordinal: ForwardOrdinal,
        value: FixedQ4816.One
    );
    private static PlayerIntent Channel(int ordinal, FixedQ4816 value) => default(PlayerIntent).WithChannel(
        ordinal: ordinal,
        value: value
    );
    private static PlayerIntent Descend() => Channel(
        ordinal: ForwardOrdinal,
        value: -FixedQ4816.One
    );
    private static PlayerIntent Release() => Ascend().WithChannel(
        ordinal: ReleaseOrdinal,
        value: FixedQ4816.One
    );
    private static PlayerIntent Rise() => Channel(
        ordinal: UpOrdinal,
        value: FixedQ4816.One
    );
    private static PlayerIntent Strafe() => Channel(
        ordinal: StrafeOrdinal,
        value: FixedQ4816.One
    );
    private static WorldBody JoinBody(WorldFixture fixture, int slot = 0) {
        var actor = WorldPrincipal.Seat(slot: slot);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        return fixture.Server.Body(index: actor.Index)!;
    }
    // Walks the body at the wall from open floor until it takes the wall row, returning the tick it took it on or
    // null. Forward (yaw 0) is -Z, straight at the wall's near face; strafe runs parallel to it.
    private static int? DriveIntoWall(WorldFixture fixture, WorldBody body, int ticks = 240, bool strafe = false) {
        Pose(
            body: body,
            y: 0f,
            z: 0f
        );

        for (var tick = 0; (tick < ticks); tick++) {
            body.SubmitIntent(intent: (strafe
                ? Strafe()
                : Ascend()));
            fixture.Step();

            if (string.Equals(
                a: body.HoldName,
                b: "wall",
                comparisonType: StringComparison.Ordinal
            )) {
                return tick;
            }
        }

        return null;
    }
    private static void Hold(WorldFixture fixture, WorldBody body, PlayerIntent intent, int ticks, string? untilNot = null) {
        for (var tick = 0; (tick < ticks); tick++) {
            body.SubmitIntent(intent: intent);
            fixture.Step();

            if (
                (untilNot is not null) &&
                !string.Equals(
                a: body.HoldName,
                b: untilNot,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return;
            }
        }
    }
    private static void Pose(WorldBody body, float y, float z, float x = 0f) => body.Pose(
        pitchRadians: 0f,
        rollRadians: 0f,
        x: x,
        y: y,
        yawRadians: 0f,
        z: z
    );

    [Fact]
    public void DrivingIntoTheWall_TakesTheWallRow_NotTheGroundRowUnderTheFeet() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(), Air()]));
        var body = JoinBody(fixture: fixture);

        Assert.NotNull(@object: DriveIntoWall(
            body: body,
            fixture: fixture
        ));

        var start = body.FixedPosition.Y;

        Hold(
            body: body,
            fixture: fixture,
            intent: Ascend(),
            ticks: 24
        );

        // The discriminator between the wall row and the ground row the body is standing on — both are the same
        // holdable placement, and the floor is the nearer surface. A ground row's tangent plane is horizontal, so
        // forward would walk along it; only a wall row rises.
        Assert.Equal(expected: "wall", actual: body.HoldName);
        Assert.True(
            condition: (((double)(body.FixedPosition.Y - start)) > 0.1),
            userMessage: $"a wall hold must ascend when forward is held; the body went from {start} to {body.FixedPosition.Y}"
        );
    }
    [Fact]
    public void DrivingALONGTheWall_KeepsTheGroundRow_WhereTheSameWorldTakesTheWallOnADriveIntoIt() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(), Air()]));
        var body = JoinBody(fixture: fixture);

        Assert.Null(@object: DriveIntoWall(
            body: body,
            fixture: fixture,
            strafe: true
        ));
        Assert.Equal(expected: "ground", actual: body.HoldName);

        var control = JoinBody(fixture: fixture, slot: 1);

        Assert.NotNull(@object: DriveIntoWall(
            body: control,
            fixture: fixture
        ));
    }
    [Fact]
    public void TheSameDriveWithNoOnDrive_NeverTakesTheWall_ThoughTheWallStopsItJustTheSame() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(onDrive: false), Air()]));
        var body = JoinBody(fixture: fixture);

        Assert.Null(@object: DriveIntoWall(
            body: body,
            fixture: fixture
        ));
        Assert.Equal(expected: "ground", actual: body.HoldName);
        Assert.NotEqual(expected: FixedVector3.Zero, actual: body.LastObstructionNormal);
    }
    [Fact]
    public void TheSameDriveIntoAnUNHOLDABLEPlacement_NeverTakesAnySurfaceRow() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holdable: false, holds: [Ground(), Wall(), Air()]));
        var body = JoinBody(fixture: fixture);

        Assert.Null(@object: DriveIntoWall(
            body: body,
            fixture: fixture
        ));
        // Nothing is holdable, so no surface probe can ever answer and the free row is the only one left.
        Assert.Equal(expected: "air", actual: body.HoldName);
    }
    [Fact]
    public void AWallRowAscendsAtItsAuthoredSpeed_WhereHalfTheSpeedRisesHalfAsFar() {
        static double Rise(float speed) {
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(speed: speed), Air()]));
            var body = JoinBody(fixture: fixture);

            Assert.NotNull(@object: DriveIntoWall(
                body: body,
                fixture: fixture
            ));

            var start = body.FixedPosition.Y;

            Hold(
                body: body,
                fixture: fixture,
                intent: Ascend(),
                ticks: 24
            );

            Assert.Equal(expected: "wall", actual: body.HoldName);

            return ((double)(body.FixedPosition.Y - start));
        }

        var seconds = (((double)(24UL * Fixtures.StepTicks)) / EngineTicks.PerSecond);
        var fast = Rise(speed: 2f);
        var slow = Rise(speed: 1f);

        Assert.True(
            condition: (Math.Abs(value: (fast - (2.0 * seconds))) < 0.02),
            userMessage: $"a hold ascending at the authored 2 u/s should have risen ~{(2.0 * seconds)}; rose {fast}"
        );
        Assert.True(
            condition: (Math.Abs(value: (slow - (1.0 * seconds))) < 0.02),
            userMessage: $"a hold ascending at the authored 1 u/s should have risen ~{(1.0 * seconds)}; rose {slow}"
        );
    }
    [Fact]
    public void ClimbingPastTheWallTop_ArrivesStandingOnIt_WhereAWorldWithNoGroundRowFallsInstead() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(), Air()]));
        var body = JoinBody(fixture: fixture);

        Assert.NotNull(@object: DriveIntoWall(
            body: body,
            fixture: fixture
        ));
        Hold(
            body: body,
            fixture: fixture,
            intent: Ascend(),
            ticks: 480,
            untilNot: "wall"
        );
        Hold(
            body: body,
            fixture: fixture,
            intent: default,
            ticks: 60
        );

        Assert.Equal(expected: "ground", actual: body.HoldName);
        Assert.True(condition: body.Grounded, userMessage: $"arriving over the lip ends standing; it ended at {body.FixedPosition}");
        // Standing at the ledge's own height is the claim: the only other walkable surface here is the floor at
        // y 0, two metres down.
        Assert.True(
            condition: (Math.Abs(value: (((double)body.FixedPosition.Y) - WallTop)) < 0.2),
            userMessage: $"an arriving body stands at the ledge height {WallTop}; it ended at {body.FixedPosition}"
        );
        Assert.True(
            condition: (((double)body.FixedPosition.Z) < WallFaceZ),
            userMessage: $"an arriving body must end over the wall's footprint; it ended at {body.FixedPosition}"
        );

        using var control = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Wall(), Air()]));
        var falling = JoinBody(fixture: control);

        Assert.NotNull(@object: DriveIntoWall(
            body: falling,
            fixture: control
        ));
        Hold(
            body: falling,
            fixture: control,
            intent: Ascend(),
            ticks: 480,
            untilNot: "wall"
        );
        Hold(
            body: falling,
            fixture: control,
            intent: default,
            ticks: 120
        );

        Assert.Equal(expected: "air", actual: falling.HoldName);
        Assert.True(
            condition: (((double)falling.FixedPosition.Y) < (WallTop / 2.0)),
            userMessage: $"with no row admitting the top face the body falls back; it ended at {falling.FixedPosition}"
        );
    }
    [Fact]
    public void DescendingToTheFloor_EndsTheWallRow_WhereTheSameDescentWellAboveItKeepsTheRow() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(), Air()]));
        var body = JoinBody(fixture: fixture);

        Assert.NotNull(@object: DriveIntoWall(
            body: body,
            fixture: fixture
        ));
        Hold(
            body: body,
            fixture: fixture,
            intent: Ascend(),
            ticks: 60
        );

        var high = body.FixedPosition.Y;

        Assert.Equal(expected: "wall", actual: body.HoldName);
        Assert.True(condition: (((double)high) > 0.2), userMessage: "the body should have left the floor before the descent");
        Hold(
            body: body,
            fixture: fixture,
            intent: Descend(),
            ticks: 5
        );
        Assert.Equal(expected: "wall", actual: body.HoldName);
        Hold(
            body: body,
            fixture: fixture,
            intent: Descend(),
            ticks: 240,
            untilNot: "wall"
        );

        Assert.Equal(expected: "ground", actual: body.HoldName);
        Assert.True(
            condition: (((double)body.FixedPosition.Y) < ((double)high)),
            userMessage: $"the descended body should be back at the floor; it is at {body.FixedPosition}"
        );
    }
    [Fact]
    public void TheReleaseChannelDropsTheRow_WhereTheIdenticalTickWithoutItKeepsIt() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(), Air()]));
        var body = JoinBody(fixture: fixture);

        Assert.NotNull(@object: DriveIntoWall(
            body: body,
            fixture: fixture
        ));
        Hold(
            body: body,
            fixture: fixture,
            intent: Ascend(),
            ticks: 30
        );

        Assert.Equal(expected: "wall", actual: body.HoldName);

        var held = body.FixedPosition.Y;

        // Release carries the SAME forward drive the control arm carries, so the drive is not the discriminator.
        body.SubmitIntent(intent: Release());
        fixture.Step();

        Assert.NotEqual(expected: "wall", actual: body.HoldName);

        using var control = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(), Air()]));
        var keeper = JoinBody(fixture: control);

        Assert.NotNull(@object: DriveIntoWall(
            body: keeper,
            fixture: control
        ));
        Hold(
            body: keeper,
            fixture: control,
            intent: Ascend(),
            ticks: 31
        );

        Assert.Equal(expected: "wall", actual: keeper.HoldName);

        Hold(
            body: body,
            fixture: fixture,
            intent: default,
            ticks: 120
        );
        Assert.True(
            condition: (body.FixedPosition.Y < held),
            userMessage: $"a released body falls; it was at {held} and is at {body.FixedPosition.Y}"
        );
    }
    [Fact]
    public void SpendDrainsTheSlotAndDropsTheRowAtItsFloor_WhereARowSpendingNothingKeepsHolding() {
        var spend = new WorldHoldSpend(RatePerSecond: 4f, State: "stamina");

        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(spend: spend), Air()], stamina: 0.25f));
        var spender = JoinBody(fixture: fixture);

        Assert.NotNull(@object: DriveIntoWall(
            body: spender,
            fixture: fixture
        ));
        Assert.NotNull(@object: spender.HoldSpendRemaining);

        var dropped = false;

        for (var tick = 0; (tick < 60); tick++) {
            spender.SubmitIntent(intent: Ascend());
            fixture.Step();
            dropped |= !string.Equals(
                a: spender.HoldName,
                b: "wall",
                comparisonType: StringComparison.Ordinal
            );
        }

        Assert.True(condition: dropped, userMessage: "a spent slot must drop the row that spends it");

        using var control = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(), Air()], stamina: 0.25f));
        var endless = JoinBody(fixture: control);

        Assert.NotNull(@object: DriveIntoWall(
            body: endless,
            fixture: control
        ));
        for (var tick = 0; (tick < 60); tick++) {
            endless.SubmitIntent(intent: Ascend());
            control.Step();

            Assert.Equal(expected: "wall", actual: endless.HoldName);
        }

        Assert.Null(@object: endless.HoldSpendRemaining);
    }
    [Fact]
    public void AFullConeTakesACeilingFace_WhereAWalkableConeCannot() {
        // Posed under the ceiling slab and far above the floor, so the only face any probe can reach is overhead.
        // The cone is the whole discriminator: the two worlds differ in nothing else.
        static string? CeilingHold(float coneMax) {
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(
                ceiling: true,
                holds: [Wall(coneMax: coneMax, coneMin: 0f, onDrive: false, reach: 1.5f, release: null), Air()]
            ));
            var body = JoinBody(fixture: fixture);

            Pose(
                body: body,
                y: (CeilingY - 1.5f),
                z: 10f
            );
            Hold(
                body: body,
                fixture: fixture,
                intent: default,
                ticks: 4
            );

            return body.HoldName;
        }

        Assert.Equal(expected: "wall", actual: CeilingHold(coneMax: 180f));
        Assert.Equal(expected: "air", actual: CeilingHold(coneMax: 60f));
    }
    [Fact]
    public void AFreeRowWithFullLiftHovers_WhereTheSameRowWithNoLiftFalls_AndMoveUpClimbs() {
        static (double Drop, double Climb) Fly(float lift) {
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Air(kind: BodyHoldKind.Lift, lift: lift, thrust: 1f)]));
            var body = JoinBody(fixture: fixture);

            Pose(
                body: body,
                y: 6f,
                z: 10f
            );
            Hold(
                body: body,
                fixture: fixture,
                intent: default,
                ticks: 60
            );

            var hovered = ((double)body.FixedPosition.Y);

            Hold(
                body: body,
                fixture: fixture,
                intent: Rise(),
                ticks: 60
            );

            return ((6.0 - hovered), (((double)body.FixedPosition.Y) - hovered));
        }

        var hovering = Fly(lift: 1f);
        var falling = Fly(lift: 0f);

        Assert.True(condition: (hovering.Drop < 0.01), userMessage: $"full lift must hold the body where it is; it dropped {hovering.Drop}");
        Assert.True(condition: (falling.Drop > 0.5), userMessage: $"no lift must fall under the same gravity; it dropped {falling.Drop}");
        Assert.True(condition: (hovering.Climb > 0.5), userMessage: $"MoveUp must climb a hovering body; it moved {hovering.Climb}");
    }
    [Fact]
    public void UpLeanLeansAPullingBodysATTITUDE_WhileItsContactAxisStaysWithGravity() {
        static (FixedVector3 Attitude, FixedVector3 Contact) OnWall(float upLean) {
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Wall(upLean: upLean), Ground(), Air()]));
            var body = JoinBody(fixture: fixture);

            Assert.NotNull(@object: DriveIntoWall(
                body: body,
                fixture: fixture
            ));
            // Clear of the floor first — resting at the wall's foot is standing, which outlives the row — then rest
            // on the row (authored ahead of the ground row, so resting keeps it) long enough for the turn to finish:
            // the lean is reached by turning over the body span, not in the tick the row is taken. The turn's own
            // law is below.
            Hold(
                body: body,
                fixture: fixture,
                intent: Ascend(),
                ticks: 60
            );
            Hold(
                body: body,
                fixture: fixture,
                intent: default,
                ticks: 300
            );

            Assert.Equal(expected: "wall", actual: body.HoldName);

            return (body.FixedOrientation.Rotate(vector: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            )), body.FixedUp);
        }

        var upright = OnWall(upLean: 0f);
        var leaned = OnWall(upLean: 1f);

        // The lean is the whole discriminator between the two arms: same wall, same drive, same ascent.
        Assert.Equal(expected: FixedQ4816.One, actual: upright.Attitude.Y);
        // The fixture wall's near face faces +Z, so a fully leaned body is DRAWN standing on that normal.
        Assert.True(
            condition: (((double)leaned.Attitude.Z) > 0.9),
            userMessage: $"a fully leaned body's attitude up is the face normal; it read {leaned.Attitude}"
        );
        // And its CONTACT axis is not: a pull holds the body, gravity does not, so the axis the solver grounds and
        // depenetrates against stays where the ambient resolve put it under both arms.
        Assert.Equal(expected: FixedQ4816.One, actual: upright.Contact.Y);
        Assert.Equal(expected: FixedQ4816.One, actual: leaned.Contact.Y);
    }
    [Fact]
    public void APullingBodyLeanedOntoACeiling_KeepsTheFloorSolidBeneathIt_AndReturnsToItOnRelease() {
        // The defect this law pins: leaning the CONTACT axis onto a ceiling tells the solver the floor is a ceiling
        // and that falling is upward, so the body sinks through the floor it is standing over. The control is the
        // same world at upLean 0, which never had an inverted axis to begin with — both must keep the floor.
        static (double Held, double Released, FixedVector3 Attitude) UnderCeiling(float upLean) {
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(
                ceiling: true,
                holds: [Wall(coneMax: 180f, coneMin: 100f, onDrive: false, reach: 1.5f, release: "release", upLean: upLean), Ground(), Air()]
            ));
            var body = JoinBody(fixture: fixture);

            Pose(
                body: body,
                y: (CeilingY - 1.5f),
                z: 10f
            );
            // A half turn, floor-up to ceiling-down, at the rate the body turns over its own span.
            Hold(
                body: body,
                fixture: fixture,
                intent: default,
                ticks: 600
            );

            Assert.Equal(expected: "wall", actual: body.HoldName);

            var held = ((double)body.FixedPosition.Y);
            var attitude = body.FixedOrientation.Rotate(vector: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            ));

            // Let go and fall: the floor at y 0 is the only thing that can stop the body, and it only stops it if
            // its own contact still reads as ground.
            Hold(
                body: body,
                fixture: fixture,
                intent: Channel(
                    ordinal: ReleaseOrdinal,
                    value: FixedQ4816.One
                ),
                ticks: 480
            );

            return (held, ((double)body.FixedPosition.Y), attitude);
        }

        var leaned = UnderCeiling(upLean: 1f);
        var upright = UnderCeiling(upLean: 0f);

        // Hanging, not fallen: the floor is at y 0 and the pull is the only thing between the body and it.
        Assert.True(condition: (leaned.Held > 3.5), userMessage: $"a ceiling pull holds the body up; it hung at {leaned.Held}");
        Assert.True(condition: (upright.Held > 3.5), userMessage: $"the control must hang too; it hung at {upright.Held}");
        Assert.True(
            condition: (((double)leaned.Attitude.Y) < -0.9),
            userMessage: $"a fully leaned body under a ceiling is drawn upside down; its attitude up read {leaned.Attitude}"
        );
        Assert.Equal(expected: FixedQ4816.One, actual: upright.Attitude.Y);
        // The claim: BOTH land on the floor. Before the fix the leaned arm fell through it.
        Assert.True(
            condition: (Math.Abs(value: leaned.Released) < 0.2),
            userMessage: $"a released leaned body must land on the floor at y 0; it ended at {leaned.Released}"
        );
        Assert.True(
            condition: (Math.Abs(value: upright.Released) < 0.2),
            userMessage: $"the control must land on the floor at y 0; it ended at {upright.Released}"
        );
    }
    [Fact]
    public void TheFactMask_ReportsClimbingOnAWallRow_AndFlyingOnALiftRow() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(), Air()]));
        var body = JoinBody(fixture: fixture);

        Assert.Equal(expected: BodyFacts.None, actual: (body.Facts & (BodyFacts.Climbing | BodyFacts.Flying)));
        Assert.DoesNotContain(actualString: body.DescribeWhere(index: 0), expectedSubstring: "climbing");
        Assert.NotNull(@object: DriveIntoWall(
            body: body,
            fixture: fixture
        ));
        Hold(
            body: body,
            fixture: fixture,
            intent: Ascend(),
            ticks: 20
        );

        Assert.Equal(expected: BodyFacts.Climbing, actual: (body.Facts & BodyFacts.Climbing));
        Assert.Contains(actualString: body.DescribeWhere(index: 0), expectedSubstring: "climbing");
        Assert.Equal(expected: BodyFactVocabulary.Describe(facts: body.Facts), actual: body.DescribeWhere(index: 0).Split(separator: "facts=")[1].Split(separator: " home=")[0]);

        using var flight = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Air(kind: BodyHoldKind.Lift, lift: 1f)]));
        var flier = JoinBody(fixture: flight);

        Pose(
            body: flier,
            y: 6f,
            z: 10f
        );
        Hold(
            body: flier,
            fixture: flight,
            intent: default,
            ticks: 4
        );

        Assert.Equal(expected: BodyFacts.Flying, actual: (flier.Facts & BodyFacts.Flying));
        Assert.Equal(expected: BodyFacts.None, actual: (flier.Facts & BodyFacts.Climbing));
    }
    [Fact]
    public void AHeldWallRow_IsEchoedByBodyHold_AndSurvivesAReplayReDriveWhereOmittingTheDriveDiverges() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(), Air()]));
        var body = JoinBody(fixture: fixture);

        Assert.NotNull(@object: DriveIntoWall(
            body: body,
            fixture: fixture
        ));
        Assert.Equal(expected: "wall", actual: body.HoldName);
        Assert.NotEqual(expected: FixedVector3.Zero, actual: body.HoldNormal);
        Assert.NotEqual(expected: FixedVector3.Zero, actual: body.HoldAnchor);

        static ulong[] DriveHashTrace(bool drive) => Fixtures.DriveHashTrace(
            document: BuildHoldDocument(holds: [Ground(), Wall(spend: new WorldHoldSpend(RatePerSecond: 0.5f, State: "stamina")), Air()], stamina: 4f),
            ticks: 240,
            join: static fixture => JoinBody(fixture: fixture),
            perTick: (advancing, tick) => advancing.SubmitIntent(intent: (drive
                ? Ascend()
                : default))
        );

        var first = DriveHashTrace(drive: true);

        Assert.Equal(actual: DriveHashTrace(drive: true), expected: first);
        Assert.NotEqual(actual: DriveHashTrace(drive: false), expected: first);
    }
    [Fact]
    public void AHeldRowSurvivesACheckpointRestore_AndContinuesBitIdentically() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(spend: new WorldHoldSpend(RatePerSecond: 0.5f, State: "stamina")), Air()], stamina: 4f));
        var uninterrupted = JoinBody(fixture: fixture);

        Assert.NotNull(@object: DriveIntoWall(
            body: uninterrupted,
            fixture: fixture
        ));
        Hold(
            body: uninterrupted,
            fixture: fixture,
            intent: Ascend(),
            ticks: 17
        );

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: new WorldAuthorityHostRowCheckpoint(
                AnnouncedCrossingHolds: [],
                AppliedTransferHighWater: null,
                AppliedTransferIds: [],
                ElapsedEngineTicks: 0,
                ForwardedBodies: [],
                FreshCounter: 0,
                InDoubtTransfers: [],
                IsPaused: false,
                NextTransferId: 1,
                PortalOccupancy: [],
                Retained: false,
                ScheduleAccumulatorTicks: 0,
                SeededArrivals: []
            ),
            reason: out var captureRefusal
        ), userMessage: captureRefusal);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!),
            checkpoint: out var decoded,
            reason: out var decodeRefusal
        ), userMessage: decodeRefusal);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-hold-checkpoint-").FullName;

        using var machines = new WorldMachineHost(engines: [], screens: definition.Screens);

        try {
            var (restoredServer, _) = WorldServer.FromCheckpoint(
                checkpoint: decoded,
                instanceIdentity: "hold-checkpoint",
                machines: machines,
                profiles: new WorldOwnedWorlds(
                    directory: stateDirectory,
                    machineId: Guid.NewGuid(),
                    template: definition
                )
            );
            var restored = restoredServer.Body(index: 0)!;

            Assert.Equal(expected: "wall", actual: restored.HoldName);
            Assert.Equal(expected: uninterrupted.HoldNormal, actual: restored.HoldNormal);
            Assert.Equal(expected: uninterrupted.HoldAnchor, actual: restored.HoldAnchor);
            Assert.Equal(expected: uninterrupted.HoldSpendRemaining, actual: restored.HoldSpendRemaining);
            // The whole integrator continues from the restore, not merely the hold identity: the spend remainder and
            // every accumulator ride the same residue.
            Assert.Equal(expected: uninterrupted.CaptureIntegrationResidue(), actual: restored.CaptureIntegrationResidue());

            var elapsed = 0UL;
            var nextTick = fixture.Server.NextInputTick;

            for (var step = 0; (step < 9); step++) {
                uninterrupted.SubmitIntent(intent: Ascend());
                restored.SubmitIntent(intent: Ascend());
                elapsed = checked((elapsed + Fixtures.StepTicks));

                var context = new FixedStepContext(ElapsedTicks: elapsed, StepTicks: Fixtures.StepTicks, Tick: nextTick++);

                fixture.Server.Step(context: in context);
                restoredServer.Step(context: in context);

                Assert.Equal(expected: WorldReplaySnapshot.HashState(population: fixture.Server.Population), actual: WorldReplaySnapshot.HashState(population: restoredServer.Population));
                Assert.Equal(expected: uninterrupted.CaptureIntegrationResidue(), actual: restored.CaptureIntegrationResidue());
            }
        } finally {
            if (Directory.Exists(path: stateDirectory)) {
                Directory.Delete(
                    path: stateDirectory,
                    recursive: true
                );
            }
        }
    }
    [Fact]
    public void AWanderingInhabitantSteersAgainstItsOwnHome_NotTheWorldOrigin() {
        // Two inhabitant rows, identical in everything but where they stand: one at the origin, one far from it.
        // Before the producer measured against the body's own home, both converged on (0, 0) — the far one's whole
        // journey WAS the defect. The near row is the control: it must be unmoved by the change, since its home IS
        // the origin the old pull aimed at.
        var document = BuildHoldDocument(holds: [Ground(), Air()]);
        var creations = document.Creations.ToList();

        creations.Add(item: BuildRoom(ceiling: false) with { Id = "floor2" });

        var placements = document.PlacementsRaw!.Rows!.ToList();

        placements.Add(item: Den(
            id: "near",
            x: 0f,
            z: 0f
        ));
        placements.Add(item: Den(
            id: "far",
            x: 30f,
            z: 30f
        ));

        using var fixture = Fixtures.FreshServer(definition: (document with {
            PopulationRaw = (document.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 2) }),
            PlacementsRaw = (document.PlacementsRaw! with { Rows = placements }),
        }));

        var first = fixture.Server.Population.EntryBody(index: WorldBodiesLimits.LocalSeatCount)!;
        var second = fixture.Server.Population.EntryBody(index: (WorldBodiesLimits.LocalSeatCount + 1))!;
        // Addressed by the home each row activated it at rather than by index: which row seeds which slot is the
        // population's business, and this law is about the home, not the seating.
        var far = ((((double)first.FixedHome.X) > 15.0)
            ? first
            : second
        );
        var near = ReferenceEquals(objA: far, objB: first)
            ? second
            : first;

        Assert.True(condition: (((double)near.FixedHome.X) < 1.0), userMessage: $"the near inhabitant's home is its own placement; it read {near.FixedHome}");
        Assert.True(condition: (((double)far.FixedHome.X) > 29.0), userMessage: $"the far inhabitant's home is its own placement; it read {far.FixedHome}");

        for (var tick = 0; (tick < 1200); tick++) {
            fixture.Step();
        }

        static double Planar(FixedVector3 from, FixedVector3 to) {
            var x = ((double)(from.X - to.X));
            var z = ((double)(from.Z - to.Z));

            return Math.Sqrt(d: ((x * x) + (z * z)));
        }

        var farFromHome = Planar(from: far.FixedPosition, to: far.FixedHome);
        var farFromOrigin = Planar(from: far.FixedPosition, to: FixedVector3.Zero);

        Assert.True(
            condition: (farFromHome < farFromOrigin),
            userMessage: $"a wanderer keeps to its own ground: it ended {farFromHome} from home and {farFromOrigin} from the origin, at {far.FixedPosition}"
        );
        // The discriminating number: the old pull would have carried it the whole 42 units to the origin.
        Assert.True(
            condition: (farFromOrigin > 20.0),
            userMessage: $"the far inhabitant drifted to the world origin — it ended {farFromOrigin} from it"
        );
        Assert.True(
            condition: (Planar(from: near.FixedPosition, to: near.FixedHome) < 20.0),
            userMessage: $"the control inhabitant must still keep to its own ground; it ended at {near.FixedPosition}"
        );
    }
    [Fact]
    public void BodyWhereEchoesTheHomeAWandererSteersAgainst() {
        var document = BuildHoldDocument(holds: [Ground(), Air()]);
        var placements = document.PlacementsRaw!.Rows!.ToList();

        placements.Add(item: Den(
            id: "far",
            x: 12f,
            z: -4f
        ));

        using var fixture = Fixtures.FreshServer(definition: (document with {
            PopulationRaw = (document.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1) }),
            PlacementsRaw = (document.PlacementsRaw! with { Rows = placements }),
        }));

        var body = fixture.Server.Population.EntryBody(index: WorldBodiesLimits.LocalSeatCount)!;

        var home = body.FixedHome.ToVector3();
        var echoed = $"home=({home.X:0.00}, {home.Y:0.00}, {home.Z:0.00})";

        // The home is the placement plus the row's own distribution sample, so the echo is asserted against what the
        // body actually holds — the claim is that body.where reports it at all, and that a teleport never moves it.
        Assert.True(condition: ((((double)body.FixedHome.X) > 11.5) && (((double)body.FixedHome.X) < 12.5)), userMessage: $"the home is the placement it was activated at; it read {body.FixedHome}");
        Assert.Contains(actualString: body.DescribeWhere(index: WorldBodiesLimits.LocalSeatCount), expectedSubstring: echoed);

        // A teleport puts the body somewhere; it does not move where the body is from.
        Pose(
            body: body,
            x: -20f,
            y: 4f,
            z: 9f
        );

        Assert.Contains(actualString: body.DescribeWhere(index: WorldBodiesLimits.LocalSeatCount), expectedSubstring: echoed);
        Assert.Contains(actualString: body.DescribeWhere(index: WorldBodiesLimits.LocalSeatCount), expectedSubstring: "pos=(-20.00, 4.00, 9.00)");
    }
    [Fact]
    public void ASurfaceRowWithNoCone_RefusesValidation_WhereTheSameRowWithOneIsAdmitted() {
        var admitted = BuildHoldDocument(holds: [Ground(), Wall(), Air()]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: admitted,
            reason: out var admittedReason
        ), userMessage: admittedReason);

        var denied = BuildHoldDocument(holds: [(Wall() with { Cone = null }), Air()]);

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(
            definition: denied,
            reason: out var deniedReason
        ),
            userMessage: "a surface hold with no cone was expected to refuse"
        );
        Assert.Contains(actualString: deniedReason, expectedSubstring: "cone");
    }
    [Fact]
    public void AConeOutsideZeroToOneEightyOrNonIncreasing_RefusesValidation() {
        foreach (var cone in new[] { new Vector2(x: -1f, y: 60f), new Vector2(x: 0f, y: 181f), new Vector2(x: 90f, y: 90f), new Vector2(x: 120f, y: 60f) }) {
            var denied = BuildHoldDocument(holds: [(Wall() with { Cone = cone }), Air()]);

            Assert.False(
                condition: WorldDefinitionValidator.TryValidateLocally(
                definition: denied,
                reason: out var reason
            ),
                userMessage: $"cone [{cone.X}, {cone.Y}] was expected to refuse"
            );
            Assert.Contains(actualString: reason, expectedSubstring: "cone");
        }
    }
    [Fact]
    public void AFreeRowCarryingACone_RefusesValidation_WhereTheSameRowWithoutOneIsAdmitted() {
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: BuildHoldDocument(holds: [Air()]),
            reason: out var admittedReason
        ), userMessage: admittedReason);
        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(
            definition: BuildHoldDocument(holds: [(Air() with { Cone = new Vector2(x: 0f, y: 60f) })]),
            reason: out var deniedReason
        ),
            userMessage: "a free hold carrying a cone was expected to refuse"
        );
        Assert.Contains(actualString: deniedReason, expectedSubstring: "cone");
    }
    [Fact]
    public void AnUndeclaredReleaseChannelOrSpendSlot_RefusesValidationByName() {
        var deniedChannel = BuildHoldDocument(holds: [Ground(), Wall(release: "nope"), Air()]);

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(
            definition: deniedChannel,
            reason: out var channelReason
        ),
            userMessage: "a release naming no declared channel was expected to refuse"
        );
        Assert.Contains(actualString: channelReason, expectedSubstring: "release");

        var deniedSlot = BuildHoldDocument(holds: [Ground(), Wall(spend: new WorldHoldSpend(RatePerSecond: 1f, State: "nope")), Air()]);

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(
            definition: deniedSlot,
            reason: out var slotReason
        ),
            userMessage: "a spend naming no declared state slot was expected to refuse"
        );
        Assert.Contains(actualString: slotReason, expectedSubstring: "spend.state");

        // The control: the same spend against a declared slot is admitted.
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: BuildHoldDocument(holds: [Ground(), Wall(spend: new WorldHoldSpend(RatePerSecond: 1f, State: "stamina")), Air()], stamina: 4f),
            reason: out var admittedReason
        ), userMessage: admittedReason);
    }
    [Fact]
    public void APullsLeanIsTurnedIntoOverTheBodySpan_NotSnappedTo_WhereAnUnleanedPullNeverLeavesGravityUp() {
        // The drawn axis turns at speed over span (rad/s), so with the fixture's capsule and the wall row's 2 m/s a
        // quarter turn takes most of a second: after sixty climbing ticks the axis is visibly short of the face, the
        // trace never turns back, and it finishes inside 300 rested ticks (1.25 s at the engine's 240 Hz; resting
        // needs the body clear of the floor, where standing would outlive the row). The control is the same climb
        // with no lean, whose drawn axis never leaves gravity-up at all.
        static List<double> AttitudeTowardFace(float upLean) {
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Wall(upLean: upLean), Ground(), Air()]));
            var body = JoinBody(fixture: fixture);

            Assert.NotNull(@object: DriveIntoWall(
                body: body,
                fixture: fixture
            ));
            Hold(
                body: body,
                fixture: fixture,
                intent: Ascend(),
                ticks: 60
            );

            var trace = new List<double>();

            for (var tick = 0; (tick < 300); tick++) {
                Hold(
                    body: body,
                    fixture: fixture,
                    intent: default,
                    ticks: 1
                );
                trace.Add(item: ((double)body.FixedOrientation.Rotate(vector: new FixedVector3(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.Zero
                )).Z));
            }

            Assert.Equal(expected: "wall", actual: body.HoldName);

            return trace;
        }

        var leaned = AttitudeTowardFace(upLean: 1f);
        var upright = AttitudeTowardFace(upLean: 0f);

        // Still turning after the sixty climbing ticks plus one at rest — a snap would already read the face here.
        Assert.True(condition: (leaned[0] < 0.9), userMessage: $"the drawn axis must still be turning on the first rested tick; it read {leaned[0]}");
        Assert.True(condition: (leaned[^1] > 0.9), userMessage: $"the turn must have finished inside the rested span; it read {leaned[^1]}");

        for (var tick = 1; (tick < leaned.Count); tick++) {
            Assert.True(condition: (leaned[tick] >= (leaned[tick - 1] - 1e-4)), userMessage: $"the turn never reverses; tick {tick} read {leaned[tick]} after {leaned[tick - 1]}");
        }

        Assert.All(collection: upright, action: z => Assert.True(condition: (Math.Abs(value: z) < 1e-3), userMessage: $"an unleaned pull's drawn axis stays gravity-up; it read {z}"));
    }
    [Fact]
    public void LettingGoMidClimb_CarriesTheClimbsRiseIntoTheFall_WhereLettingGoAtRestFallsFromRest() {
        // A pull owns the whole tangent-plane velocity, rise included; the tick it ends that rise is split into the
        // ballistic channel rather than replaced by the next planar shape. The control is the same release from
        // the same row at rest, which has no rise to carry and falls at once.
        static double RiseAfterRelease(bool climbing) {
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Wall(), Ground(), Air()]));
            var body = JoinBody(fixture: fixture);

            Assert.NotNull(@object: DriveIntoWall(
                body: body,
                fixture: fixture
            ));
            Hold(
                body: body,
                fixture: fixture,
                intent: Ascend(),
                ticks: 15
            );
            Hold(
                body: body,
                fixture: fixture,
                intent: (climbing
                    ? Ascend()
                    : default),
                ticks: 10
            );

            Assert.Equal(expected: "wall", actual: body.HoldName);

            var held = body.FixedPosition.Y;

            body.SubmitIntent(intent: Channel(
                ordinal: ReleaseOrdinal,
                value: FixedQ4816.One
            ));
            fixture.Step();

            Assert.NotEqual(expected: "wall", actual: body.HoldName);

            Hold(
                body: body,
                fixture: fixture,
                intent: default,
                ticks: 2
            );

            return ((double)(body.FixedPosition.Y - held));
        }

        var carried = RiseAfterRelease(climbing: true);
        var dropped = RiseAfterRelease(climbing: false);

        Assert.True(condition: (carried > 0d), userMessage: $"a body letting go mid-climb keeps rising for a moment; it moved {carried}");
        Assert.True(condition: (dropped <= 0d), userMessage: $"a body letting go at rest falls from rest; it moved {dropped}");
    }

    // Raw fixed-point trace helpers: position, planar velocity, vertical velocity, and yaw, hex per component —
    // the same shape DriveLawTests pins its own trace to.
    private static string Hex(FixedQ4816 value) => value.Value.ToString(format: "x16", provider: System.Globalization.CultureInfo.InvariantCulture);
    private static string TraceLine(WorldBody body) {
        var state = body.CaptureTransferState();
        var position = body.FixedPosition;

        return string.Join(separator: ' ', value: [
            Hex(value: position.X), Hex(value: position.Y), Hex(value: position.Z),
            Hex(value: state.PlanarVelocity.X), Hex(value: state.PlanarVelocity.Y), Hex(value: state.PlanarVelocity.Z),
            Hex(value: state.VerticalVelocity), Hex(value: body.FixedYaw),
        ]);
    }
    private static int MovedTicks(string[] control, string[] perturbed) {
        var moved = 0;

        for (var tick = 0; (tick < control.Length); tick++) {
            if (!string.Equals(a: control[tick], b: perturbed[tick], comparisonType: StringComparison.Ordinal)) {
                moved++;
            }
        }

        return moved;
    }

    // The walker's own rise/fall/terminal, pinned from the wall-pull-release-carries-momentum arc: climb, let go,
    // rise on the carried momentum, fall, and land — the rise phase this trace exists to pin.
    private static readonly string[] WalkerJumpTrace240 = [
        "0000000000000000 0000000000000963 fffffffffffe0aab 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000000b85 fffffffffffe099a 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000000da7 fffffffffffe0889 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000000fc9 fffffffffffe0778 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 00000000000011eb fffffffffffe0667 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000140d fffffffffffe0556 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000001630 fffffffffffe0445 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000001852 fffffffffffe0334 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000001a74 fffffffffffe0223 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000001c96 fffffffffffe0112 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000001eb8 fffffffffffe0001 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 00000000000020da fffffffffffdfef0 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 00000000000022fc fffffffffffdfddf 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000251f fffffffffffdfcce 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000002741 fffffffffffdfbbd 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000002963 fffffffffffdfaac 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000002b85 fffffffffffdf99b 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000002da7 fffffffffffdf88a 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000002fc9 fffffffffffdf779 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 00000000000031eb fffffffffffdf668 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000340d fffffffffffdf557 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000003630 fffffffffffdf446 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000003852 fffffffffffdf335 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000003a74 fffffffffffdf224 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000003c96 fffffffffffdf113 0000000000000000 0000000000020000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000003ea8 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000001f112 0000000000000000",
        "0000000000000000 00000000000040aa fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000001e223 0000000000000000",
        "0000000000000000 000000000000429d fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000001d334 0000000000000000",
        "0000000000000000 000000000000447f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000001c445 0000000000000000",
        "0000000000000000 0000000000004652 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000001b556 0000000000000000",
        "0000000000000000 0000000000004814 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000001a667 0000000000000000",
        "0000000000000000 00000000000049c7 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000019778 0000000000000000",
        "0000000000000000 0000000000004b6a fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000018889 0000000000000000",
        "0000000000000000 0000000000004cfc fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000001799a 0000000000000000",
        "0000000000000000 0000000000004e7f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000016aab 0000000000000000",
        "0000000000000000 0000000000004ff2 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000015bbc 0000000000000000",
        "0000000000000000 0000000000005155 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000014ccd 0000000000000000",
        "0000000000000000 00000000000052a8 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000013dde 0000000000000000",
        "0000000000000000 00000000000053eb fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000012eef 0000000000000000",
        "0000000000000000 000000000000551f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000012000 0000000000000000",
        "0000000000000000 0000000000005642 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000011112 0000000000000000",
        "0000000000000000 0000000000005755 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000010223 0000000000000000",
        "0000000000000000 0000000000005859 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000000f334 0000000000000000",
        "0000000000000000 000000000000594c fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000000e445 0000000000000000",
        "0000000000000000 0000000000005a30 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000000d556 0000000000000000",
        "0000000000000000 0000000000005b03 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000000c667 0000000000000000",
        "0000000000000000 0000000000005bc7 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000000b778 0000000000000000",
        "0000000000000000 0000000000005c7b fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000000a889 0000000000000000",
        "0000000000000000 0000000000005d1f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 000000000000999a 0000000000000000",
        "0000000000000000 0000000000005db2 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000008aab 0000000000000000",
        "0000000000000000 0000000000005e36 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000007bbc 0000000000000000",
        "0000000000000000 0000000000005eab fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000006ccd 0000000000000000",
        "0000000000000000 0000000000005f0f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000005dde 0000000000000000",
        "0000000000000000 0000000000005f63 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000004eef 0000000000000000",
        "0000000000000000 0000000000005fa7 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000004000 0000000000000000",
        "0000000000000000 0000000000005fdb fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000003112 0000000000000000",
        "0000000000000000 0000000000006000 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000002223 0000000000000000",
        "0000000000000000 0000000000006014 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000001334 0000000000000000",
        "0000000000000000 0000000000006019 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000445 0000000000000000",
        "0000000000000000 000000000000600f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffff556 0000000000000000",
        "0000000000000000 0000000000005fe9 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffffdccd 0000000000000000",
        "0000000000000000 0000000000005fa9 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffffc445 0000000000000000",
        "0000000000000000 0000000000005f4f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffffabbc 0000000000000000",
        "0000000000000000 0000000000005edb fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffff9334 0000000000000000",
        "0000000000000000 0000000000005e4d fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffff7aab 0000000000000000",
        "0000000000000000 0000000000005da5 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffff6223 0000000000000000",
        "0000000000000000 0000000000005ce2 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffff499a 0000000000000000",
        "0000000000000000 0000000000005c05 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffff3112 0000000000000000",
        "0000000000000000 0000000000005b0f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffff1889 0000000000000000",
        "0000000000000000 00000000000059fd fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 ffffffffffff0000 0000000000000000",
        "0000000000000000 00000000000058d2 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffee778 0000000000000000",
        "0000000000000000 000000000000578d fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffeceef 0000000000000000",
        "0000000000000000 000000000000562d fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffeb667 0000000000000000",
        "0000000000000000 00000000000054b4 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffe9dde 0000000000000000",
        "0000000000000000 0000000000005320 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffe8556 0000000000000000",
        "0000000000000000 0000000000005172 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffe6ccd 0000000000000000",
        "0000000000000000 0000000000004fa9 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffe5445 0000000000000000",
        "0000000000000000 0000000000004dc7 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffe3bbc 0000000000000000",
        "0000000000000000 0000000000004bca fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffe2334 0000000000000000",
        "0000000000000000 00000000000049b4 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffe0aab 0000000000000000",
        "0000000000000000 0000000000004783 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffdf223 0000000000000000",
        "0000000000000000 0000000000004538 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffdd99a 0000000000000000",
        "0000000000000000 00000000000042d2 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffdc112 0000000000000000",
        "0000000000000000 0000000000004053 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffda889 0000000000000000",
        "0000000000000000 0000000000003db9 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffd9000 0000000000000000",
        "0000000000000000 0000000000003b05 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffd7778 0000000000000000",
        "0000000000000000 0000000000003838 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffd5eef 0000000000000000",
        "0000000000000000 000000000000354f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffd4667 0000000000000000",
        "0000000000000000 000000000000324d fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffd2dde 0000000000000000",
        "0000000000000000 0000000000002f31 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffd1556 0000000000000000",
        "0000000000000000 0000000000002bfa fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffcfccd 0000000000000000",
        "0000000000000000 00000000000028a9 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffce445 0000000000000000",
        "0000000000000000 000000000000253e fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffccbbc 0000000000000000",
        "0000000000000000 00000000000021b9 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffcb334 0000000000000000",
        "0000000000000000 0000000000001e1a fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffc9aab 0000000000000000",
        "0000000000000000 0000000000001a60 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffc8223 0000000000000000",
        "0000000000000000 000000000000168d fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffc699a 0000000000000000",
        "0000000000000000 000000000000129f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffc5112 0000000000000000",
        "0000000000000000 0000000000000e97 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffc3889 0000000000000000",
        "0000000000000000 0000000000000a75 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffc2000 0000000000000000",
        "0000000000000000 0000000000000639 fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 fffffffffffc0778 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000000051f fffffffffffdf113 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
    ];

    [Fact]
    public void WalkerJumpAndLand_ReproducesTheRecordedTrace_WhereRiseChangedDiverges() {
        static string[] Trace(float rise) {
            var lines = new string[240];
            var gravity = new WorldHoldGravity(Fall: 23f, Rise: rise);
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Wall(), Ground(gravity: gravity), Air(gravity: gravity)]));
            var body = JoinBody(fixture: fixture);
            var tick = 0;

            Assert.NotNull(@object: DriveIntoWall(body: body, fixture: fixture));
            for (; (tick < 25); tick++) {
                body.SubmitIntent(intent: Ascend());
                fixture.Step();
                lines[tick] = TraceLine(body: body);
            }

            body.SubmitIntent(intent: Channel(ordinal: ReleaseOrdinal, value: FixedQ4816.One));
            fixture.Step();
            lines[tick] = TraceLine(body: body);
            tick++;

            for (; (tick < 240); tick++) {
                body.SubmitIntent(intent: default);
                fixture.Step();
                lines[tick] = TraceLine(body: body);
            }

            return lines;
        }

        Assert.Equal(expected: WalkerJumpTrace240, actual: Trace(rise: 14f));

        var moved = MovedTicks(control: WalkerJumpTrace240, perturbed: Trace(rise: 20f));

        Assert.True(condition: (moved > 0), userMessage: "a row's own rise must move the trace, or the trace pins nothing about it");
    }

    // The Free Lift row's own hover-then-climb arc: full lift holds altitude, then MoveUp climbs at the row's
    // thrust fraction of the resolved move speed.
    private static readonly string[] FreeLiftHoverTrace240 = [
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000060ccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000061111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000061555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000061999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000061ddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000062222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000062666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000062aaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000062eee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000063333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000063777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000063bbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000064000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000064444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000064888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000064ccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000065111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000065555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000065999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000065ddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000066222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000066666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000066aaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000066eee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000067333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000067777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000067bbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000068000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000068444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000068888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000068ccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000069111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000069555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000069999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000069ddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006a222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006a666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006aaaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006aeee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006b333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006b777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006bbbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006c000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006c444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006c888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006cccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006d111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006d555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006d999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006dddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006e222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006e666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006eaaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006eeee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006f333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006f777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000006fbbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000070000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000070444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000070888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000070ccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000071111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000071555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000071999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000071ddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000072222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000072666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000072aaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000072eee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000073333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000073777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000073bbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000074000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000074444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000074888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000074ccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000075111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000075555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000075999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000075ddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000076222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000076666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000076aaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000076eee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000077333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000077777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000077bbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000078000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000078444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000078888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000078ccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000079111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000079555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000079999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000079ddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007a222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007a666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007aaaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007aeee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007b333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007b777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007bbbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007c000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007c444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007c888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007cccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007d111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007d555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007d999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007dddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007e222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007e666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007eaaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007eeee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007f333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007f777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000007fbbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000080000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000080444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000080888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000080ccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000081111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000081555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000081999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000081ddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000082222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000082666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000082aaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000082eee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000083333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000083777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000083bbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000084000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000084444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000084888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000084ccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000085111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000085555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000085999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000085ddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000086222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000086666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000086aaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000086eee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000087333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000087777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000087bbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000088000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000088444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000088888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000088ccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000089111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000089555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000089999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000089ddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008a222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008a666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008aaaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008aeee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008b333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008b777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008bbbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008c000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008c444 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008c888 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008cccc 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008d111 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008d555 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008d999 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008dddd 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008e222 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008e666 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008eaaa 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008eeee 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008f333 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008f777 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 000000000008fbbb 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
        "0000000000000000 0000000000090000 00000000000a0000 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0000000000000000",
    ];

    [Fact]
    public void FreeLiftHoverWithMoveUp_ReproducesTheRecordedTrace_WhereThrustChangedDiverges() {
        static string[] Trace(float thrust) {
            var lines = new string[240];
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Air(kind: BodyHoldKind.Lift, lift: 1f, thrust: thrust)]));
            var body = JoinBody(fixture: fixture);

            Pose(body: body, y: 6f, z: 10f);

            for (var tick = 0; (tick < 60); tick++) {
                body.SubmitIntent(intent: default);
                fixture.Step();
                lines[tick] = TraceLine(body: body);
            }
            for (var tick = 60; (tick < 240); tick++) {
                body.SubmitIntent(intent: Rise());
                fixture.Step();
                lines[tick] = TraceLine(body: body);
            }

            return lines;
        }

        Assert.Equal(expected: FreeLiftHoverTrace240, actual: Trace(thrust: 1f));

        var moved = MovedTicks(control: FreeLiftHoverTrace240, perturbed: Trace(thrust: 0.5f));

        Assert.True(condition: (moved > 0), userMessage: "a row's own thrust must move the trace, or the trace pins nothing about it");
    }
    [Fact]
    public void ARowWithNoThrust_IgnoresMoveUp_WhereTheSameRowWithFullThrustClimbs() {
        static double Climb(float thrust) {
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Air(kind: BodyHoldKind.Lift, lift: 1f, thrust: thrust)]));
            var body = JoinBody(fixture: fixture);

            Pose(body: body, y: 6f, z: 10f);
            Hold(body: body, fixture: fixture, intent: default, ticks: 60);

            var hovered = ((double)body.FixedPosition.Y);

            Hold(body: body, fixture: fixture, intent: Rise(), ticks: 60);

            return (((double)body.FixedPosition.Y) - hovered);
        }

        Assert.True(condition: (Climb(thrust: 0f) < 0.01), userMessage: "a row with no thrust must ignore MoveUp");
        Assert.True(condition: (Climb(thrust: 1f) > 0.5), userMessage: "the same row with full thrust must climb on MoveUp");
    }
    [Fact]
    public void APullRowAuthoringGravity_RefusesValidation_WhereTheSameRowWithoutOneIsAdmitted() {
        var admitted = BuildHoldDocument(holds: [Ground(), Wall(), Air()]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);

        var denied = BuildHoldDocument(holds: [(Wall() with { Gravity = new WorldHoldGravity(Fall: 1f, Rise: 1f) }), Ground(), Air()]);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "gravity is refused");
    }
    [Fact]
    public void AMotionKindKitAuthoringNoHolds_RefusesValidation_WhereTheSameKitWithOneRowIsAdmitted() {
        var admitted = BuildHoldDocument(holds: [Air()]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);

        var denied = BuildHoldDocument(holds: []);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "holds is required for a Motion-kind body motion program");
    }
    [Fact]
    public void AHoldListWithOnlyASurfaceRow_RefusesValidation_WhereTheSameListWithAFreeRowIsAdmitted() {
        var admitted = BuildHoldDocument(holds: [Ground(), Air()]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);

        var denied = BuildHoldDocument(holds: [Ground()]);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "authors no unconditional row");
    }
    [Fact]
    public void AThrustCarryingRowOnAWorldWithNoMoveUpChannel_RefusesValidation_WhereTheSameRowWithOneIsAdmitted() {
        var admitted = BuildHoldDocument(holds: [Air(thrust: 1f)]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);

        var denied = admitted with {
            ChannelsRaw = [.. admitted.Channels.Where(predicate: channel => (channel.Role != ChannelRole.MoveUp))],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "thrust is positive but the world declares no MoveUp channel");
    }
    [Fact]
    public void AProgramSelectingApplyHoldWithoutResolveHold_RefusesValidation() {
        var admitted = BuildHoldDocument(holds: [Air()]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);

        var programs = admitted.BodyMotionPrograms.ToList();
        var index = programs.FindIndex(match: program => string.Equals(a: program.Name, b: "hold", comparisonType: StringComparison.Ordinal));

        programs[index] = programs[index] with {
            Operations = [.. programs[index].Operations.Where(predicate: op => (op != BodyMotionOp.ResolveHold))],
        };

        var denied = admitted with { BodyMotionProgramsRaw = programs };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "selects 'ApplyHold' without 'ResolveHold'");
    }
    /// <summary>A body that leaned on a surface hold carries that fact through a checkpoint, so the restored body
    /// turns its drawn axis back the way the live one does when the hold ends. The control is a body in the same
    /// world that never took a surface row: its axis is seated outright, so the two returns differ.</summary>
    [Fact]
    public void ALeanedBodyRestoredFromACheckpoint_ReturnsItsDrawnAxisLikeTheLiveBody_WhereANeverLeanedBodySeatsItInstead() {
        using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Wall(upLean: 1f), Ground(), Air()]));
        var uninterrupted = JoinBody(fixture: fixture);

        Assert.NotNull(@object: DriveIntoWall(
            body: uninterrupted,
            fixture: fixture
        ));
        Hold(
            body: uninterrupted,
            fixture: fixture,
            intent: Ascend(),
            ticks: 60
        );

        Assert.Equal(expected: "wall", actual: uninterrupted.HoldName);

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: new WorldAuthorityHostRowCheckpoint(
                AnnouncedCrossingHolds: [],
                AppliedTransferHighWater: null,
                AppliedTransferIds: [],
                ElapsedEngineTicks: 0,
                ForwardedBodies: [],
                FreshCounter: 0,
                InDoubtTransfers: [],
                IsPaused: false,
                NextTransferId: 1,
                PortalOccupancy: [],
                Retained: false,
                ScheduleAccumulatorTicks: 0,
                SeededArrivals: []
            ),
            reason: out var captureRefusal
        ), userMessage: captureRefusal);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!),
            checkpoint: out var decoded,
            reason: out var decodeRefusal
        ), userMessage: decodeRefusal);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-lean-checkpoint-").FullName;

        using var machines = new WorldMachineHost(engines: [], screens: definition.Screens);

        try {
            var (restoredServer, _) = WorldServer.FromCheckpoint(
                checkpoint: decoded,
                instanceIdentity: "lean-checkpoint",
                machines: machines,
                profiles: new WorldOwnedWorlds(
                    directory: stateDirectory,
                    machineId: Guid.NewGuid(),
                    template: definition
                )
            );
            var restored = restoredServer.Body(index: 0)!;

            // The flag rides the residue, so the restored body is leaned before it takes a single step.
            Assert.Equal(expected: uninterrupted.CaptureIntegrationResidue(), actual: restored.CaptureIntegrationResidue());

            // Let go on both and watch the axis come back: the live body turns over its own span, and the restored
            // body must turn through the same arc rather than seating.
            var elapsed = 0UL;
            var nextTick = fixture.Server.NextInputTick;
            var seated = 0;

            for (var step = 0; (step < 12); step++) {
                var release = Channel(
                    ordinal: ReleaseOrdinal,
                    value: FixedQ4816.One
                );

                uninterrupted.SubmitIntent(intent: release);
                restored.SubmitIntent(intent: release);
                elapsed = checked((elapsed + Fixtures.StepTicks));

                var context = new FixedStepContext(ElapsedTicks: elapsed, StepTicks: Fixtures.StepTicks, Tick: nextTick++);

                fixture.Server.Step(context: in context);
                restoredServer.Step(context: in context);

                Assert.Equal(expected: uninterrupted.CaptureIntegrationResidue(), actual: restored.CaptureIntegrationResidue());

                if (((double)Up(body: restored).Y) > 0.999) {
                    seated++;
                }
            }

            // The turn takes longer than the twelve ticks watched, which is what makes it a turn and not a seat.
            Assert.Equal(expected: 0, actual: seated);
            Assert.True(
                condition: (((double)Up(body: restored).Y) < 0.999),
                userMessage: $"the restored body must still be turning its axis back; it read {Up(body: restored)}"
            );

            // The control: a body in the same world that never leaned. Its drawn axis is seated to ambient, so it
            // reads upright on the very first tick the same release is submitted.
            using var never = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Wall(upLean: 1f), Ground(), Air()]));
            var flat = JoinBody(fixture: never);

            Hold(
                body: flat,
                fixture: never,
                intent: default,
                ticks: 1
            );

            Assert.Null(@object: flat.HoldName is "wall" ? "wall" : null);
            Assert.True(
                condition: (((double)Up(body: flat).Y) > 0.999),
                userMessage: $"a body that never leaned is drawn upright at once; it read {Up(body: flat)}"
            );
        } finally {
            if (Directory.Exists(path: stateDirectory)) {
                Directory.Delete(
                    path: stateDirectory,
                    recursive: true
                );
            }
        }
    }
    private static FixedVector3 Up(WorldBody body) => body.FixedOrientation.Rotate(vector: new FixedVector3(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    ));
}
