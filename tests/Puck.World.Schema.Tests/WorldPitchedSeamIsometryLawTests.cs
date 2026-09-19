using System.Numerics;
using Puck.Maths;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the arrival isometry of a PITCHED reciprocal pair — a boundary lying flat beneath a deck,
/// which is what a sky island's rim is made of. <see cref="WorldAdjacencyBoundary.CompileFrame"/> derives the frames
/// and <see cref="WorldFrameIsometry"/> maps through them, so the pair's meaning is proven here in fixed point
/// rather than through the composition root; the orchestration around it is verified by running <c>Puck.World</c>.
/// </summary>
/// <remarks>The invariant under test: the arrival turn about world up is <c>destinationYaw - sourceYaw - 180</c>,
/// whatever the boundaries' pitch. Two faces pointing at each other map as the identity — a body leaving through one
/// arrives at the same world point under the other authority, keeping its heading, its planar velocity and its
/// vertical velocity — and a flat pair authored at one yaw is a seam authored with a half turn in it.</remarks>
public sealed class WorldPitchedSeamIsometryLawTests {
    private static readonly FixedVector3 WorldUp = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );

    // One in-plane dot product plus one recomposition, both in Q48.16: an axis-aligned pair is bit-exact, and a
    // non-cardinal yaw rounds. One raw unit is 1/65536 of a world unit.
    private const long ErrorBudgetRaw = 24;

    private static WorldAdjacencyBoundary Boundary(float yaw, float pitch, float height = 0f) => new(
        Center: new Vector3(
            x: 0f,
            y: height,
            z: 0f
        ),
        OutwardYawDegrees: yaw,
        OutwardPitchDegrees: pitch,
        Width: 90f,
        Height: 90f
    );
    private static FixedVector3 Fixed(double x, double y, double z) => new(
        X: FixedQ4816.FromDouble(value: x),
        Y: FixedQ4816.FromDouble(value: y),
        Z: FixedQ4816.FromDouble(value: z)
    );
    private static long RawAbs(FixedQ4816 a, FixedQ4816 b) => Math.Abs(value: (a.Value - b.Value));
    private static long RawAbs(FixedVector3 a, FixedVector3 b) => Math.Max(
        val1: RawAbs(
            a: a.X,
            b: b.X
        ),
        val2: Math.Max(
            val1: RawAbs(
                a: a.Y,
                b: b.Y
            ),
            val2: RawAbs(
                a: a.Z,
                b: b.Z
            )
        )
    );

    /// <summary>A flat pair whose two faces point at each other hands a falling body straight down: same world
    /// point, same heading, same velocity, at any authored yaw.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(180f)]
    [InlineData(270f)]
    public void FlatPairFacingItsCounterpart_HandsAFallingBodyStraightDown(float yaw) {
        var source = Boundary(
            height: 80f,
            pitch: -90f,
            yaw: yaw
        ).CompileFrame();
        var destination = Boundary(
            height: 80f,
            pitch: 90f,
            yaw: (yaw + 180f)
        ).CompileFrame();
        var position = Fixed(
            x: 4.25,
            y: 79.76,
            z: -13.54
        );
        var planar = Fixed(
            x: 0.5,
            y: 0,
            z: -6.5
        );
        var vertical = FixedQ4816.FromDouble(value: -17.25);
        var travelerYaw = FixedQ4816.FromDouble(value: 0.75);
        var mapped = WorldFrameIsometry.MapArrival(
            destination: in destination,
            source: in source,
            travelerPlanarVelocity: planar,
            travelerPosition: position,
            travelerVerticalVelocity: vertical,
            travelerYawRadians: travelerYaw
        );

        Assert.True(
            condition: (RawAbs(
                a: mapped.Position,
                b: position
            ) <= ErrorBudgetRaw),
            userMessage: $"arrival {mapped.Position} is not beneath the point of fall {position}"
        );
        Assert.True(
            condition: (RawAbs(
                a: mapped.YawRadians,
                b: travelerYaw
            ) <= ErrorBudgetRaw),
            userMessage: $"arrival yaw {(double)mapped.YawRadians} turned away from {(double)travelerYaw}"
        );
        Assert.True(
            condition: (RawAbs(
                a: mapped.PlanarVelocity,
                b: planar
            ) <= ErrorBudgetRaw),
            userMessage: $"arrival planar velocity {mapped.PlanarVelocity} is not the departure's {planar}"
        );
        Assert.True(
            condition: (RawAbs(
                a: mapped.VerticalVelocity,
                b: vertical
            ) <= ErrorBudgetRaw),
            userMessage: $"arrival vertical velocity {(double)mapped.VerticalVelocity} is not the departure's {(double)vertical}"
        );
    }
    /// <summary>A flat pair authored at one yaw on both sides is a seam with a half turn in it: the arrival is the
    /// departure point rotated a half turn about the boundary's own centre, facing the other way. The turn is the
    /// authored yaw delta, so this is the pair a rim must not author.</summary>
    [Fact]
    public void FlatPairAuthoredAtOneYaw_TurnsTheTravelerAHalfTurn() {
        var source = Boundary(
            height: 80f,
            pitch: -90f,
            yaw: 0f
        ).CompileFrame();
        var destination = Boundary(
            height: 80f,
            pitch: 90f,
            yaw: 0f
        ).CompileFrame();
        var mapped = WorldFrameIsometry.MapArrival(
            destination: in destination,
            source: in source,
            travelerPlanarVelocity: Fixed(
                x: 0.5,
                y: 0,
                z: -6.5
            ),
            travelerPosition: Fixed(
                x: 4.25,
                y: 79.76,
                z: -13.54
            ),
            travelerVerticalVelocity: FixedQ4816.FromDouble(value: -17.25),
            travelerYawRadians: FixedQ4816.Zero
        );

        Assert.Equal(
            actual: mapped.Position,
            expected: Fixed(
                x: -4.25,
                y: 79.76,
                z: 13.54
            )
        );
        Assert.Equal(
            actual: mapped.PlanarVelocity,
            expected: Fixed(
                x: -0.5,
                y: 0,
                z: 6.5
            )
        );
        Assert.Equal(
            actual: mapped.VerticalVelocity,
            expected: FixedQ4816.FromDouble(value: -17.25)
        );
        Assert.True(
            condition: (RawAbs(
                a: WorldFrameIsometry.YawDelta(
                    destination: in destination,
                    source: in source
                ),
                b: FixedQ4816.FromDouble(value: Math.PI)
            ) <= ErrorBudgetRaw),
            userMessage: "a same-yaw flat pair's arrival turn is a half turn about world up"
        );
    }
    /// <summary>Every reciprocal pair this map admits carries world up to world up, whatever its pitch — the
    /// condition the pair validator refuses by name, and the reason a crossing never rolls a body.</summary>
    [Theory]
    [InlineData(-90f, 0f)]
    [InlineData(-90f, 90f)]
    [InlineData(-45f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(45f, 180f)]
    public void ReciprocalPair_CarriesWorldUpToWorldUp(float pitch, float yaw) {
        var source = Boundary(
            pitch: pitch,
            yaw: yaw
        ).CompileFrame();
        var destination = Boundary(
            pitch: -pitch,
            yaw: (yaw + 180f)
        ).CompileFrame();

        Assert.Equal(
            actual: WorldFrameIsometry.MapVector(
                destination: in destination,
                source: in source,
                value: WorldUp
            ),
            expected: WorldUp
        );
    }
    /// <summary>A pair with one flat face and one wall does not carry world up, which is what the validator reads to
    /// refuse it: the two authorities would disagree about which way gravity points.</summary>
    [Fact]
    public void HalfFlatPair_DoesNotCarryWorldUp() {
        var flat = Boundary(
            pitch: -90f,
            yaw: 0f
        ).CompileFrame();
        var wall = Boundary(
            pitch: 0f,
            yaw: 180f
        ).CompileFrame();

        Assert.NotEqual(
            actual: WorldFrameIsometry.MapVector(
                destination: in wall,
                source: in flat,
                value: WorldUp
            ),
            expected: WorldUp
        );
    }
    /// <summary>An arrival lands strictly inside the destination's owned half-space, by as much as the crossing went
    /// past the source's own ownership threshold. A crossing fires only outside that threshold and the map negates
    /// the signed distance along the boundary normal, so the two facts compose: the reverse hand-off of a body
    /// climbing out through a floor seam cannot arrive on the side its new authority does not own.</summary>
    [Theory]
    [InlineData(0.0328)]
    [InlineData(0.6867)]
    public void ArrivalLandsAtLeastOneOwnershipThresholdInsideTheDestination(double threshold) {
        var source = Boundary(
            height: 80f,
            pitch: 90f,
            yaw: 0f
        ).CompileFrame();
        var destination = Boundary(
            height: 80f,
            pitch: -90f,
            yaw: 180f
        ).CompileFrame();
        var outward = FixedQ4816.FromDouble(value: threshold);
        var crossing = WorldAdjacencyRegion.Sweep(
            frame: source,
            from: Fixed(
                x: 1.5,
                y: 79.5,
                z: -2.25
            ),
            to: Fixed(
                x: 1.5,
                y: (80.0 + threshold + 0.25),
                z: -2.25
            ),
            outwardThreshold: outward
        );

        Assert.True(
            condition: crossing.Crossed,
            userMessage: "a rise clear past the threshold did not cross"
        );

        var arrival = WorldFrameIsometry.MapPoint(
            destination: in destination,
            point: Fixed(
                x: 1.5,
                y: (80.0 + threshold + 0.25),
                z: -2.25
            ),
            source: in source
        );
        var inward = -FixedVector3.Dot(
            left: (arrival - destination.Origin),
            right: destination.Normal
        );

        Assert.True(
            condition: (inward >= outward),
            userMessage: $"the arrival sits {((double)inward)} inside the destination, less than its own ownership threshold {threshold}"
        );
    }
    /// <summary>Pitch is an elevation at every authored angle, not only at the cardinal quarter turns: a positive
    /// pitch raises the outward normal toward world up and tips the rectangle's own up axis away from it.</summary>
    [Theory]
    [InlineData(30f)]
    [InlineData(45f)]
    [InlineData(60f)]
    public void NonCardinalPitch_RaisesTheOutwardNormal(float pitch) {
        var up = Boundary(
            pitch: pitch,
            yaw: 0f
        ).CompileFrame();
        var down = Boundary(
            pitch: -pitch,
            yaw: 0f
        ).CompileFrame();

        Assert.True(
            condition: (up.Normal.Y > FixedQ4816.Zero),
            userMessage: $"an outward pitch of +{pitch} must raise the normal toward world up; got {up.Normal}"
        );
        Assert.True(
            condition: (down.Normal.Y < FixedQ4816.Zero),
            userMessage: $"an outward pitch of -{pitch} must lower the normal toward world down; got {down.Normal}"
        );
        Assert.True(
            condition: (up.Normal.Z > FixedQ4816.Zero),
            userMessage: $"an outward yaw of 0 keeps the normal's horizontal part on +Z; got {up.Normal}"
        );
        Assert.True(
            condition: (up.Up.Z < FixedQ4816.Zero),
            userMessage: $"a face tipped up carries its own up axis toward -Z at yaw 0; got {up.Up}"
        );
    }
}
