using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Controller laws at a crest and on a descent, independent of authored art and frame sampling.</summary>
public sealed class GroundSnapLawTests {
    private sealed class Slope(bool crest) : IContactField {
        public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
            up = FixedVector3.UnitY;
            return true;
        }
        public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity,
            in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
            var normal = ((crest && (position.Z <= FixedQ4816.Zero)) ? FixedVector3.UnitY
                : new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One,
                    Z: FixedQ4816.FromDouble(value: (crest ? 0.25 : -0.25))).Normalize());

            return new FixedStaticContactSolver(ContactSkin: FixedQ4816.FromDouble(value: 0.001),
                GroundedThreshold: FixedQ4816.FromDouble(value: 0.5), MaxIterations: 4)
                .Resolve(colliders: [FixedStaticCollider.HalfSpace(point: FixedVector3.Zero, normal: normal)],
                    dynamicColliders: [], position: ref position, velocity: ref velocity,
                    orientation: in orientation, volumes: volumes, up: in up);
        }
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AWalkerStaysOnTheSurfaceAcrossACrestAndDownASlope(bool crest) {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildGradientUpDocument(gradientUp: false));
        var body = fixture.JoinSeat();

        body.Pose(pitchRadians: 0f, rollRadians: 0f, x: 20f, y: (crest ? -0.5f : 0.5f), yawRadians: 0f, z: 2f);
        body.SetContactField(field: new Slope(crest: crest));
        for (var tick = 0; (tick < 20); tick++) { fixture.Step(); }
        Assert.True(condition: body.Grounded);
        for (var tick = 0; (tick < (Fixtures.DefaultRateHz * 2)); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One));
            fixture.Step();
            Assert.True(condition: body.Grounded, userMessage: $"tick {tick}: {body.FixedPosition}");
        }
        Assert.True(condition: (body.FixedPosition.Z < FixedQ4816.Zero));
        if (crest) {
            Assert.InRange(((double)body.FixedPosition.Y), -0.01, 0.02);
        } else {
            Assert.True(condition: (body.FixedPosition.Y < FixedQ4816.Zero));
        }
    }
}
