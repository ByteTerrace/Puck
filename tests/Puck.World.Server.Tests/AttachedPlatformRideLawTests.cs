using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for riding an attached solid under the analytic contact provider: a body standing on a platform whose
/// carrier moves is carried with it, rising and falling, and stays grounded the whole way.</summary>
public sealed class AttachedPlatformRideLawTests {
    private const int Carrier = 1;
    private const float CarrierAbovePlatform = 3f;
    private const float Climb = 0.0625f;
    private const int Rider = 0;
    private const int Ticks = 32;

    private static WorldDefinition Document() {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(
            Id: 0,
            Name: "floor",
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(
                x: 1f,
                y: 0.05f,
                z: 1f
            ),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "platform",
                Palette: null,
                Shapes: [shape],
                Frames: null
            ),
            source: "platform"
        );
        var platform = new WorldPrototype(
            Id: "platform",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );

        return source with {
            CollisionRaw = source.Collision with { Requirements = [] },
            CreationsRaw = [.. source.Creations, platform],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: "platform",
                    PrototypeId: platform.Id,
                    Position: Vector3.Zero,
                    YawDegrees: 0f,
                    Scale: 1f,
                    Solid: new WorldSolid(Margin: 0f),
                    Attach: new WorldPlacementAttach(
                        BodyIndex: Carrier,
                        LocalOffset: new Vector3(
                            x: 0f,
                            y: -CarrierAbovePlatform,
                            z: 0f
                        )
                    )
                ),
            ],
        };
    }
    // Stands the carrier so its platform's top lies at height + 0.05, then steps one tick.
    private static void Carry(WorldFixture fixture, float height, float yaw) {
        fixture.Server.Body(index: Carrier)!.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 20f,
            y: (height + CarrierAbovePlatform),
            yawRadians: yaw,
            z: 0f
        );
        fixture.Step();
    }

    [InlineData(0f)]
    [InlineData(0.3f)]
    [Theory]
    public void ARiderStaysGroundedOnARisingAndFallingPlatform(float yaw) {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var rider = fixture.JoinSeat(slot: Rider);

        _ = fixture.JoinSeat(slot: Carrier);

        rider.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 20f,
            y: 0.1f,
            yawRadians: 0f,
            z: 0f
        );

        for (var tick = 0; (tick < 10); tick++) {
            Carry(
                fixture: fixture,
                height: 0f,
                yaw: yaw
            );
        }

        Assert.True(condition: rider.Grounded);

        var rest = ((float)rider.FixedPosition.Y);

        for (var tick = 1; (tick <= Ticks); tick++) {
            Carry(
                fixture: fixture,
                height: (Climb * tick),
                yaw: yaw
            );
            Assert.True(
                condition: rider.Grounded,
                userMessage: $"rising tick {tick}"
            );
            Assert.Equal(
                expected: (rest + (Climb * tick)),
                actual: ((float)rider.FixedPosition.Y),
                tolerance: 0.001f
            );
        }

        for (var tick = (Ticks - 1); (tick >= 0); tick--) {
            Carry(
                fixture: fixture,
                height: (Climb * tick),
                yaw: yaw
            );
            Assert.True(
                condition: rider.Grounded,
                userMessage: $"falling tick {tick}"
            );
            Assert.Equal(
                expected: (rest + (Climb * tick)),
                actual: ((float)rider.FixedPosition.Y),
                tolerance: 0.001f
            );
        }
    }
}
