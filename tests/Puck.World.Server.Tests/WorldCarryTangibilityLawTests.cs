
using Puck.Maths;

using Xunit;
using static Puck.World.Testing.CarryFixtures;

namespace Puck.World.Server.Tests;

/// <summary>Laws for tangible carry: a carried body's own collider sweeps against static geometry every tick
/// (<c>WorldBody.FollowCarrier</c>) instead of following the carrier's frame unconditionally, and
/// <c>WorldPopulation.TryEndCarry</c> refuses a release whose left-behind pose overlaps that same geometry.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class WorldCarryTangibilityLawTests {
    private static WorldFixture JoinedCarrier(WorldDefinition definition) {
        var fixture = Fixtures.FreshServer(definition: definition);

        _ = fixture.JoinSeat();

        return fixture;
    }

    [Fact]
    public void CarrySweepBlocksAgainstAWallAndControlWithNoWallReachesTheFullOffset() {
        using var blocked = JoinedCarrier(definition: WallCarryDocument(includeWall: true));
        var blockedCarrier = blocked.Server.Body(index: CarrierIndex)!;
        var blockedBall = blocked.Server.Body(index: BallIndex)!;

        Assert.True(
            condition: blocked.Server.Population.TryBeginCarry(
                carrierIndex: CarrierIndex,
                reason: out var blockedBeginReason,
                targetIndex: BallIndex
            ),
            userMessage: blockedBeginReason
        );
        blocked.Step();

        // Walk the carrier toward the wall in small per-tick steps (never one big jump — the sweep is a continuous
        // check from the ball's own previous position, not a teleport-safe one) far enough that an UNBLOCKED ball
        // would end up on the far side of the wall's near face.
        for (var step = 0; (step < 60); step++) {
            blockedCarrier.Pose(
                pitchRadians: 0f,
                rollRadians: 0f,
                x: 0f,
                y: 0f,
                yawRadians: 0f,
                z: (-0.02f * step)
            );
            blocked.Step();
        }

        var wallNearFaceZ = FixedQ4816.FromDouble(value: -1.5d);

        Assert.True(
            condition: (blockedBall.FixedPosition.Z > wallNearFaceZ),
            userMessage: $"the carried ball embedded in the wall at z={((double)blockedBall.FixedPosition.Z):0.###} (wall face at {((double)wallNearFaceZ):0.###})"
        );

        // Control: the identical walk with no wall placement in the document reaches the carrier's own final
        // position plus the unobstructed offset — proving the blocked run above was actually resisted, not just a
        // coincidence of the walk never reaching that far.
        using var open = JoinedCarrier(definition: WallCarryDocument(includeWall: false));
        var openCarrier = open.Server.Body(index: CarrierIndex)!;
        var openBall = open.Server.Body(index: BallIndex)!;

        Assert.True(
            condition: open.Server.Population.TryBeginCarry(
                carrierIndex: CarrierIndex,
                reason: out var openBeginReason,
                targetIndex: BallIndex
            ),
            userMessage: openBeginReason
        );
        open.Step();

        for (var step = 0; (step < 60); step++) {
            openCarrier.Pose(
                pitchRadians: 0f,
                rollRadians: 0f,
                x: 0f,
                y: 0f,
                yawRadians: 0f,
                z: (-0.02f * step)
            );
            open.Step();
        }

        Assert.True(
            condition: (openBall.FixedPosition.Z < wallNearFaceZ),
            userMessage: $"the control (no wall) never reached the wall's Z band — z={((double)openBall.FixedPosition.Z):0.###}; the blocked run's stop proves nothing without this contrast"
        );
    }
    [Fact]
    public void ReleaseRefusesAnEmbeddedPoseAndControlOpenPoseSucceeds() {
        using var fixture = JoinedCarrier(definition: WallCarryDocument(includeWall: true));
        var ball = fixture.Server.Body(index: BallIndex)!;

        Assert.True(
            condition: fixture.Server.Population.TryBeginCarry(
                carrierIndex: CarrierIndex,
                reason: out var beginReason,
                targetIndex: BallIndex
            ),
            userMessage: beginReason
        );

        // Placed directly inside the wall's interior — before any tick's FollowCarrier sweep has a chance to push
        // it back out, exactly the pose a release must catch.
        ball.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 0f,
            y: 1f,
            yawRadians: 0f,
            z: -1.8f
        );

        Assert.False(condition: fixture.Server.Population.TryEndCarry(
            carrierIndex: CarrierIndex,
            reason: out var embeddedReason
        ));
        Assert.Contains(
            actualString: embeddedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "penetrates"
        );
        // A refused release leaves the relationship intact.
        Assert.Equal(
            expected: BallIndex,
            actual: fixture.Server.Body(index: CarrierIndex)!.Carrying
        );
        Assert.Equal(
            expected: CarrierIndex,
            actual: ball.CarriedBy
        );

        // Control: the SAME still-active relationship, target moved to an open pose, releases cleanly.
        ball.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 4f,
            y: 1f,
            yawRadians: 0f,
            z: -0.6f
        );

        Assert.True(
            condition: fixture.Server.Population.TryEndCarry(
                carrierIndex: CarrierIndex,
                reason: out var openReason
            ),
            userMessage: openReason
        );
        Assert.Null(@object: fixture.Server.Body(index: CarrierIndex)!.Carrying);
        Assert.Null(@object: ball.CarriedBy);
    }
    [Fact]
    public void CarryWallCorrectionWakesARestingRigidCarrier() {
        using var fixture = JoinedCarrier(definition: WallCarryDocument(
            includeWall: true,
            rigidCarrier: true
        ));
        var carrier = fixture.Server.Body(index: CarrierIndex)!;

        carrier.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 0f,
            y: 0f,
            yawRadians: 0f,
            z: -1f
        );
        var residue = carrier.CaptureIntegrationResidue();

        carrier.ApplyIntegrationResidue(residue: residue with {
            RigidResting = true,
            RigidRestingHoldTicks = 1UL,
            RigidVelocity = FixedVector3.Zero,
            RigidAngularVelocity = FixedVector3.Zero,
        });

        Assert.True(condition: carrier.Resting);
        Assert.True(
            condition: fixture.Server.Population.TryBeginCarry(
                carrierIndex: CarrierIndex,
                reason: out var beginReason,
                targetIndex: BallIndex
            ),
            userMessage: beginReason
        );

        fixture.Step();

        Assert.False(
            condition: carrier.Resting,
            userMessage: "the carried body's wall correction moved the rigid carrier without opening its exact-rest latch"
        );
    }
    [Fact]
    public void CarriedBodyPairCorrectionWakesARestingRigidCarrier() {
        using var fixture = JoinedCarrier(definition: WallCarryDocument(
            includeOtherBody: true,
            includeWall: false,
            rigidCarrier: true
        ));
        var carrier = fixture.Server.Body(index: CarrierIndex)!;
        var residue = carrier.CaptureIntegrationResidue();

        carrier.ApplyIntegrationResidue(residue: residue with {
            RigidResting = true,
            RigidRestingHoldTicks = 1UL,
            RigidVelocity = FixedVector3.Zero,
            RigidAngularVelocity = FixedVector3.Zero,
        });

        Assert.True(condition: carrier.Resting);
        Assert.True(
            condition: fixture.Server.Population.TryBeginCarry(
                carrierIndex: CarrierIndex,
                reason: out var beginReason,
                targetIndex: BallIndex
            ),
            userMessage: beginReason
        );

        fixture.Step();

        Assert.False(
            condition: carrier.Resting,
            userMessage: "the carried body's dynamic-pair correction moved the rigid carrier without opening its exact-rest latch"
        );
    }
}
