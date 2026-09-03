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

    private static WorldHold Air(BodyHoldKind kind = BodyHoldKind.Gravity, float lift = 0f) => new(
        Bond: BodyHoldBond.Free,
        Hold: kind,
        Lift: lift,
        Name: "air"
    );
    private static WorldHold Ground(float coneMax = 60f) => new(
        Bond: BodyHoldBond.Surface,
        Cone: new Vector2(x: 0f, y: coneMax),
        Hold: BodyHoldKind.Gravity,
        Name: "ground",
        Reach: 1.2f
    );
    private static WorldHold Wall(bool onDrive = true, float speed = 2f, float upLean = 0f, string? release = "release", WorldHoldSpend? spend = null, float coneMin = 60f, float coneMax = 120f, float reach = 0.8f) => new(
        Bond: BodyHoldBond.Surface,
        Cone: new Vector2(x: coneMin, y: coneMax),
        DriveAlignment: 0.5f,
        Grip: 1f,
        Hold: BodyHoldKind.Grip,
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
        var grounded = kits[0].Motion!;

        kits[0] = (kits[0] with {
            Collider = new WorldCollider.Capsule(Endpoint: new Vector3(x: 0f, y: 1f, z: 0f), Radius: 0.35f),
            Motion = (grounded with { Holds = holds }),
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
                BodyMotionOp.ShapePlanarVelocity,
                BodyMotionOp.RunActionTriggers,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.ApplyVerticalDrive,
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
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Air(kind: BodyHoldKind.Lift, lift: lift)]));
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
    public void UpLeanLeansAGrippingBodysATTITUDE_WhileItsContactAxisStaysWithGravity() {
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
        // And its CONTACT axis is not: a grip holds the body, gravity does not, so the axis the solver grounds and
        // depenetrates against stays where the ambient resolve put it under both arms.
        Assert.Equal(expected: FixedQ4816.One, actual: upright.Contact.Y);
        Assert.Equal(expected: FixedQ4816.One, actual: leaned.Contact.Y);
    }
    [Fact]
    public void AGrippingBodyLeanedOntoACeiling_KeepsTheFloorSolidBeneathIt_AndReturnsToItOnRelease() {
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

        // Hanging, not fallen: the floor is at y 0 and the grip is the only thing between the body and it.
        Assert.True(condition: (leaned.Held > 3.5), userMessage: $"a ceiling grip holds the body up; it hung at {leaned.Held}");
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
    public void AGripsLeanIsTurnedIntoOverTheBodySpan_NotSnappedTo_WhereAnUnleanedGripNeverLeavesGravityUp() {
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

        Assert.All(collection: upright, action: z => Assert.True(condition: (Math.Abs(value: z) < 1e-3), userMessage: $"an unleaned grip's drawn axis stays gravity-up; it read {z}"));
    }
    [Fact]
    public void LettingGoMidClimb_CarriesTheClimbsRiseIntoTheFall_WhereLettingGoAtRestFallsFromRest() {
        // A grip owns the whole tangent-plane velocity, rise included; the tick it ends that rise is split into the
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
}
