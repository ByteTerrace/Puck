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
        var grounded = ((WorldMotionModel.Grounded)kits[0].Motion!);

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
    public void UpLeanOneSetsTheBodyUpToTheSurfaceNormal_WhereUpLeanZeroKeepsGravityUp() {
        static FixedVector3 Up(float upLean) {
            using var fixture = Fixtures.FreshServer(definition: BuildHoldDocument(holds: [Ground(), Wall(upLean: upLean), Air()]));
            var body = JoinBody(fixture: fixture);

            Assert.NotNull(@object: DriveIntoWall(
                body: body,
                fixture: fixture
            ));
            Hold(
                body: body,
                fixture: fixture,
                intent: Ascend(),
                ticks: 10
            );

            Assert.Equal(expected: "wall", actual: body.HoldName);

            return body.FixedUp;
        }

        var upright = Up(upLean: 0f);
        var leaned = Up(upLean: 1f);

        Assert.Equal(expected: FixedQ4816.One, actual: upright.Y);
        // The wall's near face faces +Z, so a fully leaned body's up axis is that normal.
        Assert.True(
            condition: (((double)leaned.Z) > 0.9),
            userMessage: $"a fully leaned body's up is the face normal; it read {leaned}"
        );
        Assert.True(
            condition: (((double)leaned.Y) < 0.1),
            userMessage: $"a fully leaned body's up carries no gravity-up; it read {leaned}"
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
        Assert.Equal(expected: BodyFactVocabulary.Describe(facts: body.Facts), actual: body.DescribeWhere(index: 0).Split(separator: "facts=")[1].TrimEnd(trimChar: ']'));

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
    public void AHoldProgramOnAModelSupplyingNoHolds_RefusesValidationNamingTheFacet() {
        var document = BuildHoldDocument(holds: [Ground(), Wall(), Air()]);
        var kits = document.Kits.ToList();

        kits[0] = (kits[0] with {
            Motion = new WorldMotionModel.Vehicle(
                Accel: 8f,
                Brake: 12f,
                CoastDrag: 4f,
                FallGravity: 30f,
                Grip: 20f,
                MaxFallSpeed: 40f,
                PitchRate: 0f,
                ReverseTopSpeed: 4f,
                RiseGravity: 20f,
                SteerFalloff: 0.5f,
                SteerRate: 2f,
                SteerReferenceSpeed: 6f,
                TopSpeed: 12f
            ),
        });

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(
            definition: (document with { KitRowsRaw = kits }),
            reason: out var reason
        ),
            userMessage: "a vehicle model paired with a holds program was expected to refuse"
        );
        Assert.Contains(actualString: reason, expectedSubstring: "Holds");
    }
}
