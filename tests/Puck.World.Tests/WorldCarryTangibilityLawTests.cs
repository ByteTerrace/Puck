using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for tangible carry: a carried body's own collider sweeps against static geometry every tick
/// (<c>WorldBody.FollowCarrier</c>) instead of following the carrier's frame unconditionally, and
/// <c>WorldPopulation.TryEndCarry</c> refuses a release whose left-behind pose overlaps that same geometry.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class WorldCarryTangibilityLawTests {
    private const int CarrierIndex = 0;
    private const int BallIndex = WorldBodiesLimits.LocalSeatCount;

    // A carrier (Carry facet only) and a rigid "ball" target, plus — unlike WorldCarryCommandLawTests' own fixture —
    // a solid field requirement and a wall placement the ball's own witness-sweep can actually collide with. The
    // carrier starts at the origin; its own carry offset (0, 1, -0.6) is what the ball rides at with no wall in the
    // way, so a wall placed further along -Z is what the sweep is exercised against.
    private static WorldDefinition WallCarryDocument(bool includeWall) {
        var source = Fixtures.BuildDocument();
        var carrierKit = source.Kits[0] with {
            Carry = new WorldCarry(
                Offset: new Vector3(x: 0f, y: 1f, z: -0.6f),
                MassEquivalent: 60f,
                MaxCarryFraction: 1f,
                MaxReach: 1.5f
            ),
        };
        var ballKit = source.Kits[0] with {
            Name = "ball",
            Collider = new WorldCollider.Sphere(Radius: 0.15f),
            BodyContact = WorldBodyContactMode.Solid,
            Rigid = new WorldRigid(Mass: 0.3f, Restitution: 0.2f, Friction: 0.4f, RollingFriction: 0.1f, LinearDamping: 0f, AngularDamping: 0f),
            Carry = null,
        };
        var ballShape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: 0.15f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var ballDocument = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "carry-ball", Palette: null, Shapes: [ballShape], Frames: null);
        var ballCanonical = CreationCanonicalizer.Canonicalize(document: ballDocument, source: "carry-ball");
        var ballCreation = new WorldPrototype(Id: "carry-ball", Document: ballCanonical.Document, HashRaw: ballCanonical.Hash);

        // The wall's own near face sits at Z = -1.5, well past the unobstructed carry offset (Z = -0.6) but well
        // short of where the carrier's own walk (below) would otherwise carry it.
        var wallShape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 3f, y: 3f, z: 0.3f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var wallDocument = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "wall", Palette: null, Shapes: [wallShape], Frames: null);
        var wallCanonical = CreationCanonicalizer.Canonicalize(document: wallDocument, source: "wall");
        var wallCreation = new WorldPrototype(Id: "wall", Document: wallCanonical.Document, HashRaw: wallCanonical.Hash);

        return source with {
            KitRowsRaw = [carrierKit, ballKit],
            DefaultSeatKitRaw = carrierKit.Name,
            PopulationRaw = source.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1) },
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
            CreationsRaw = (includeWall ? [ballCreation, wallCreation] : [ballCreation]),
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: "carry-ball-placement",
                    PrototypeId: ballCreation.Id,
                    Position: new DocumentVector3(value: new Vector3(x: 0f, y: 1f, z: -0.6f)),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Inhabit: new WorldPlacementInhabit(Kit: "ball", Look: null, Source: IntentSource.Idle, Distribution: WorldDistribution.Default)
                ),
                .. (includeWall
                    ? new[] {
                        new WorldPlacement(
                            Id: "wall-placement",
                            PrototypeId: wallCreation.Id,
                            Position: new DocumentVector3(value: new Vector3(x: 0f, y: 1f, z: -1.8f)),
                            YawDegrees: 0f,
                            Scale: 1f,
                            Solid: new WorldSolid(Margin: 0f)
                        ),
                    }
                    : []),
            ],
        };
    }

    private static WorldFixture JoinedCarrier(WorldDefinition definition) {
        var fixture = Fixtures.FreshServer(definition: definition);
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        return fixture;
    }

    [Fact]
    public void CarrySweepBlocksAgainstAWallAndControlWithNoWallReachesTheFullOffset() {
        using var blocked = JoinedCarrier(definition: WallCarryDocument(includeWall: true));
        var blockedCarrier = blocked.Server.Body(index: CarrierIndex)!;
        var blockedBall = blocked.Server.Body(index: BallIndex)!;

        Assert.True(condition: blocked.Server.Population.TryBeginCarry(carrierIndex: CarrierIndex, targetIndex: BallIndex, reason: out var blockedBeginReason), userMessage: blockedBeginReason);
        blocked.Step();

        // Walk the carrier toward the wall in small per-tick steps (never one big jump — the sweep is a continuous
        // check from the ball's own previous position, not a teleport-safe one) far enough that an UNBLOCKED ball
        // would end up on the far side of the wall's near face.
        for (var step = 0; (step < 60); step++) {
            blockedCarrier.Pose(x: 0f, y: 0f, z: (-0.02f * step), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
            blocked.Step();
        }

        var wallNearFaceZ = FixedQ4816.FromDouble(value: -1.5d);

        Assert.True(condition: (blockedBall.FixedPosition.Z > wallNearFaceZ),
            userMessage: $"the carried ball embedded in the wall at z={(double)blockedBall.FixedPosition.Z:0.###} (wall face at {(double)wallNearFaceZ:0.###})");

        // Control: the identical walk with no wall placement in the document reaches the carrier's own final
        // position plus the unobstructed offset — proving the blocked run above was actually resisted, not just a
        // coincidence of the walk never reaching that far.
        using var open = JoinedCarrier(definition: WallCarryDocument(includeWall: false));
        var openCarrier = open.Server.Body(index: CarrierIndex)!;
        var openBall = open.Server.Body(index: BallIndex)!;

        Assert.True(condition: open.Server.Population.TryBeginCarry(carrierIndex: CarrierIndex, targetIndex: BallIndex, reason: out var openBeginReason), userMessage: openBeginReason);
        open.Step();

        for (var step = 0; (step < 60); step++) {
            openCarrier.Pose(x: 0f, y: 0f, z: (-0.02f * step), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
            open.Step();
        }

        Assert.True(condition: (openBall.FixedPosition.Z < wallNearFaceZ),
            userMessage: $"the control (no wall) never reached the wall's Z band — z={(double)openBall.FixedPosition.Z:0.###}; the blocked run's stop proves nothing without this contrast");
    }

    [Fact]
    public void ReleaseRefusesAnEmbeddedPoseAndControlOpenPoseSucceeds() {
        using var fixture = JoinedCarrier(definition: WallCarryDocument(includeWall: true));
        var ball = fixture.Server.Body(index: BallIndex)!;

        Assert.True(condition: fixture.Server.Population.TryBeginCarry(carrierIndex: CarrierIndex, targetIndex: BallIndex, reason: out var beginReason), userMessage: beginReason);

        // Placed directly inside the wall's interior — before any tick's FollowCarrier sweep has a chance to push
        // it back out, exactly the pose a release must catch.
        ball.Pose(x: 0f, y: 1f, z: -1.8f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        Assert.False(condition: fixture.Server.Population.TryEndCarry(carrierIndex: CarrierIndex, reason: out var embeddedReason));
        Assert.Contains(actualString: embeddedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "penetrates");
        // A refused release leaves the relationship intact.
        Assert.Equal(expected: BallIndex, actual: fixture.Server.Body(index: CarrierIndex)!.Carrying);
        Assert.Equal(expected: CarrierIndex, actual: ball.CarriedBy);

        // Control: the SAME still-active relationship, target moved to an open pose, releases cleanly.
        ball.Pose(x: 4f, y: 1f, z: -0.6f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        Assert.True(condition: fixture.Server.Population.TryEndCarry(carrierIndex: CarrierIndex, reason: out var openReason), userMessage: openReason);
        Assert.Null(@object: fixture.Server.Body(index: CarrierIndex)!.Carrying);
        Assert.Null(@object: ball.CarriedBy);
    }
}
