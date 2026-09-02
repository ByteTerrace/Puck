using Puck.Assets.Documents;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Contract under test: body-frame policy is independent of the contact provider. Without
/// <see cref="WorldContactRequirement.GradientDerivedUp"/>, solved gravity still supplies ambient up, but a measured
/// support normal cannot replace it. With the requirement, the support normal may orient a grounded body. A live
/// rebuild changing between those policies reseats on the next step when the new ambient source has a direction;
/// it does not retain the prior support-owned axis.
/// </summary>
public sealed class BodyUpPolicyLawTests {
    private const float FlankHorizontalRatio = 0.9f;
    private const float GravityMagnitude = 46f;
    // The shared ball fixture's flank ray points (0.9, sqrt(1 - 0.9²), 0) away from its origin. Accelerating along
    // the exact reverse ray makes SurfaceFollowing's airborne and grounded frame agree at the contact point.
    private static readonly DocumentVector3 FlankGravity = new(
        x: (-GravityMagnitude * FlankHorizontalRatio),
        y: (-GravityMagnitude * MathF.Sqrt(x: (1f - (FlankHorizontalRatio * FlankHorizontalRatio)))),
        z: 0f
    );
    private const int SettleTicks = 480;

    [Fact]
    public void Ambient_FollowsTiltedSolvedGravityWithoutSurfaceFollowing() {
        using var fixture = Fixtures.FreshServer(definition: WithFlankGravity(
            definition: Fixtures.BuildGradientUpDocument(gradientUp: false)
        ));
        var actor = JoinSeat(fixture: fixture);
        var body = fixture.Server.Body(index: actor.Index)!;

        for (var tick = 0; (tick < 12); tick++) {
            fixture.Step();
        }

        AssertNotYawOnly(body: body, context: "ambient frame under tilted solved gravity");
    }

    [Fact]
    public void LiveRebuildFromSurfaceFollowingToAmbient_ReseatsUpOnTheNextStep() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildGradientUpDocument(gradientUp: true));
        var actor = JoinSeat(fixture: fixture);
        var body = fixture.Server.Body(index: actor.Index)!;

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
        }

        Assert.True(condition: body.Grounded, userMessage: "the surface-following control never grounded on the ball's flank");
        AssertNotYawOnly(body: body, context: "surface-following control");

        var candidate = fixture.Server.Definition with {
            CollisionRaw = fixture.Server.Definition.Collision with {
                Requirements = [WorldContactRequirement.SmoothUnionContact],
            },
            GravityRaw = VerticalGravity,
        };
        var contentHash = WorldDefinitionFileSource.ComputeContentHash(
            content: WorldDefinitionSerialization.Serialize(definition: candidate)
        );

        fixture.Server.EnqueueRebuild(
            request: new WorldRebuildRequest(
                ContentHash: contentHash,
                Definition: candidate,
                Force: true,
                Kind: WorldRebuildKind.Load,
                PathHint: "body-up-policy-rebuild-probe.world.json"
            ),
            principal: WorldPrincipal.Console
        );
        fixture.Step();

        Assert.DoesNotContain(
            expected: WorldContactRequirement.GradientDerivedUp,
            collection: fixture.Server.Definition.Collision.Requirements
        );
        AssertYawOnly(body: body, context: "first Ambient step after the live rebuild");
    }

    private static WorldPrincipal JoinSeat(WorldFixture fixture) {
        var actor = WorldPrincipal.Seat(slot: Fixtures.GradientUpSeatSlot);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        return actor;
    }

    private static WorldDefinition WithFlankGravity(WorldDefinition definition) => definition with {
        GravityRaw = new WorldGravity(
            Attractors: [],
            GravitationalConstant: 0f,
            SofteningLength: 0.5f,
            Solver: WorldGravitySolver.Pairwise,
            Uniform: FlankGravity
        ),
    };

    private static WorldGravity VerticalGravity { get; } = new(
        Attractors: [],
        GravitationalConstant: 0f,
        SofteningLength: 0.5f,
        Solver: WorldGravitySolver.Pairwise,
        Uniform: new DocumentVector3(x: 0f, y: -GravityMagnitude, z: 0f)
    );

    private static void AssertYawOnly(WorldBody body, string context) {
        var orientation = body.FixedOrientation;

        Assert.True(
            condition: ((orientation.X == FixedQ4816.Zero) && (orientation.Z == FixedQ4816.Zero)),
            userMessage: $"{context}: expected yaw-only raw orientation, got W={orientation.W.Value} X={orientation.X.Value} Y={orientation.Y.Value} Z={orientation.Z.Value}"
        );
    }

    private static void AssertNotYawOnly(WorldBody body, string context) {
        var orientation = body.FixedOrientation;

        Assert.False(
            condition: ((orientation.X == FixedQ4816.Zero) && (orientation.Z == FixedQ4816.Zero)),
            userMessage: $"{context}: expected a surface-derived raw orientation, got W={orientation.W.Value} X={orientation.X.Value} Y={orientation.Y.Value} Z={orientation.Z.Value}"
        );
    }
}
