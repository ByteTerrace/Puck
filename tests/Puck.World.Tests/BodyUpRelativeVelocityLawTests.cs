using Puck.Maths;
using Puck.Physics;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a walking body's velocity is its planar velocity, tangent to the body's own up, plus its
/// vertical velocity along that up (<see cref="WorldBody.ApproximateWorldVelocity"/>), and every write that moves it in
/// world space splits it back against that up, never against world Y. Under a tilted up: a body-pair push leaves the
/// pushed body's world velocity exactly its velocity before less the approach along the push; an adjacency arrival with
/// nothing to sweep against keeps the world velocity it arrived with; a continuum clamp removes exactly the velocity
/// leaving the owner; and a taut tether leaves exactly the world velocity its constraint solves to.
/// </summary>
public sealed class BodyUpRelativeVelocityLawTests {
    private static FixedVector3 TiltedUp { get; } = new FixedVector3(
        X: FixedQ4816.FromDouble(value: 0.6),
        Y: FixedQ4816.FromDouble(value: 0.8),
        Z: FixedQ4816.Zero
    ).Normalize();
    // A planar velocity tangent to the tilted up, with a component along world Y, so a split against world Y moves it.
    private static FixedVector3 Tangent { get; } = new(X: -TiltedUp.Y, Y: TiltedUp.X, Z: FixedQ4816.Zero);

    // Two half-unit spheres that resolve against each other as solids.
    private static WorldDefinition SolidPairWorld() {
        var source = Fixtures.BuildDocument();

        return source with {
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.Sphere(Radius: 0.5f),
            })],
        };
    }
    // Poses a walker at the point under the given up, falling along it at the given speed, admitted to its tick as the
    // population's admission leaves it.
    private static WorldBody PoseFaller(WorldBody body, FixedVector3 at, FixedVector3 up, FixedQ4816 fallSpeed, FixedVector3? planar = null) {
        body.Pose(pitchRadians: FixedQ4816.Zero, position: at, rollRadians: FixedQ4816.Zero, yawRadians: FixedQ4816.Zero);
        body.ApplyIntegrationResidue(residue: body.CaptureIntegrationResidue() with {
            OrdinaryAdvanceAdmitted = true,
            Up = up,
            UpNeedsReseat = false,
        });
        body.ApplyTransferState(state: body.CaptureTransferState() with {
            PlanarVelocity = (planar ?? FixedVector3.Zero),
            VerticalVelocity = -fallSpeed,
        });

        return body;
    }

    [Fact]
    public void APairPushUnderATiltedUpRemovesOnlyTheApproachAlongThePush() {
        // The upper body sits half a unit along up from the lower one, so the pair overlaps by half a unit and the
        // upper body is pushed along up, against its fall. With nothing else moving it, the push must stop that fall
        // exactly: the approach is all of its velocity.
        using var fixture = Fixtures.FreshServer(definition: SolidPairWorld());
        var up = TiltedUp;
        var fallSpeed = FixedQ4816.FromInteger(value: 2L);
        var lowerAt = new FixedVector3(X: FixedQ4816.FromInteger(value: 4L), Y: FixedQ4816.FromInteger(value: 8L), Z: FixedQ4816.FromInteger(value: 4L));
        var lower = PoseFaller(at: lowerAt, body: fixture.JoinSeat(slot: 0), fallSpeed: fallSpeed, up: up);
        var upper = PoseFaller(at: (lowerAt + (up * FixedQ4816.FromDouble(value: 0.5))), body: fixture.JoinSeat(slot: 1), fallSpeed: fallSpeed, up: up);
        var upperBefore = upper.FixedPosition;
        var velocityBefore = upper.ApproximateWorldVelocity();

        Assert.Equal(actual: velocityBefore, expected: (up * -fallSpeed));

        fixture.Server.Population.ResolveDynamicContacts();

        var push = (upper.FixedPosition - upperBefore);

        Assert.True(
            condition: (FixedVector3.Dot(left: push, right: up) > FixedQ4816.Zero),
            userMessage: $"the pair did not push the upper body along up; it moved by {push}"
        );

        var normal = push.Normalize();
        var expected = (velocityBefore - (normal * FixedVector3.Dot(left: velocityBefore, right: normal)));

        Assert.Equal(expected: expected, actual: upper.ApproximateWorldVelocity());
        Assert.Equal(expected: (up * -fallSpeed), actual: lower.ApproximateWorldVelocity());
    }
    [Fact]
    public void AnArrivalWithNothingToSweepAgainstKeepsTheWorldVelocityItArrivedWith() {
        // Falling along a tilted up with a planar velocity tangent to it, in open air, the arrival's sweep moves nothing,
        // so the body's world velocity must come out of the arrival exactly as it went in.
        using var fixture = Fixtures.FreshServer(definition: SolidPairWorld());
        var step = Fixtures.StepTicksAt(rateHz: Fixtures.DefaultRateHz);
        var body = PoseFaller(at: new FixedVector3(X: FixedQ4816.FromInteger(value: 4L), Y: FixedQ4816.FromInteger(value: 8L), Z: FixedQ4816.FromInteger(value: 4L)), body: fixture.JoinSeat(slot: 0), fallSpeed: FixedQ4816.FromInteger(value: 2L), planar: Tangent, up: TiltedUp);
        var before = body.ApproximateWorldVelocity();

        body.ApplyContinuumTrajectory(
            destinationCompletedEngineTick: 0UL,
            entityIndex: 0,
            trajectory: new WorldContinuumTrajectory(
                BoundaryEvents: 1,
                ConsumedThroughEngineTick: step,
                ContinuumEndEngineTick: step,
                ContinuumStartEngineTick: 0UL,
                PreviousPosition: body.FixedPosition,
                SourceTick: 1UL
            )
        );

        Assert.Equal(expected: before, actual: body.ApproximateWorldVelocity());
    }
    [Fact]
    public void AContinuumClampRemovesExactlyTheVelocityLeavingTheOwner() {
        // The face's outward normal points down the tilted up, so the fall leaves through it and the tangent motion
        // does not: the clamp must remove the fall along the normal and keep the rest.
        using var fixture = Fixtures.FreshServer(definition: SolidPairWorld());
        var at = new FixedVector3(X: FixedQ4816.FromInteger(value: 4L), Y: FixedQ4816.FromInteger(value: 8L), Z: FixedQ4816.FromInteger(value: 4L));
        var body = PoseFaller(at: at, body: fixture.JoinSeat(slot: 0), fallSpeed: FixedQ4816.FromInteger(value: 2L), planar: Tangent, up: TiltedUp);
        var normal = -TiltedUp;
        var before = body.ApproximateWorldVelocity();
        var outward = FixedVector3.Dot(left: before, right: normal);

        Assert.True(condition: (outward > FixedQ4816.Zero), userMessage: "the control: the body's velocity leaves through the face");

        body.ClampContinuum(
            frame: new WorldFaceFrame(
                HalfDepth: FixedQ4816.One,
                HalfHeight: FixedQ4816.One,
                HalfWidth: FixedQ4816.One,
                Normal: normal,
                Origin: at,
                Right: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One),
                Up: new FixedVector3(X: -TiltedUp.Y, Y: TiltedUp.X, Z: FixedQ4816.Zero)
            ),
            seamU: FixedQ4816.Zero,
            seamV: FixedQ4816.Zero
        );

        Assert.Equal(expected: (before - (normal * outward)), actual: body.ApproximateWorldVelocity());
    }
    [Fact]
    public void ATautTetherLeavesExactlyTheWorldVelocityItsConstraintSolvesTo() {
        // Tethered to a world point two units away on a one-unit rope, the body falls along a tilted up with a planar
        // velocity tangent to it, partly away from the anchor. The population's tether pass must leave the body where
        // and moving as the constraint alone solves that world state: pulled onto the rope, the outward part removed.
        using var fixture = Fixtures.FreshServer(definition: SolidPairWorld());
        var at = new FixedVector3(X: FixedQ4816.FromInteger(value: 4L), Y: FixedQ4816.FromInteger(value: 8L), Z: FixedQ4816.FromInteger(value: 4L));
        var body = PoseFaller(at: at, body: fixture.JoinSeat(slot: 0), fallSpeed: FixedQ4816.FromInteger(value: 2L), planar: Tangent, up: TiltedUp);
        var outward = new FixedVector3(X: -FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.One).Normalize();
        var anchor = (at - (outward * FixedQ4816.FromInteger(value: 2L)));
        var expectedPosition = at;
        var expectedVelocity = body.ApproximateWorldVelocity();
        var constraint = new FixedTetherConstraint(length: FixedQ4816.One, minLength: FixedQ4816.Zero);

        Assert.True(condition: (FixedVector3.Dot(left: expectedVelocity, right: outward) > FixedQ4816.Zero), userMessage: "the control: the body moves away from the anchor");
        Assert.True(condition: constraint.Solve(anchor: in anchor, position: ref expectedPosition, velocity: ref expectedVelocity).Taut, userMessage: "the control: the rope is taut");

        body.SetTetherToWorldPoint(anchor: anchor, length: FixedQ4816.One, minLength: FixedQ4816.Zero);
        fixture.Server.Population.ResolveTethers();

        Assert.Equal(expected: expectedPosition, actual: body.FixedPosition);
        Assert.Equal(expected: expectedVelocity, actual: body.ApproximateWorldVelocity());
    }
}
