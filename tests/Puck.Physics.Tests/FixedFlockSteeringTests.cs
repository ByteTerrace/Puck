using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedFlockSteeringTests {
    private static readonly FixedQ4816 One = FixedQ4816.One;
    private static readonly FixedQ4816 Zero = FixedQ4816.Zero;
    private static readonly FixedVector3 X = new(One, Zero, Zero);
    private static readonly FixedVector3 Y = new(Zero, One, Zero);
    private static readonly FixedVector3 Z = new(Zero, Zero, One);
    private static readonly FixedFlockWeights All = new(One, One, One, One, One, One);

    [Fact]
    public void IndividualTermsHaveIndependentDirectionalWitnesses() {
        FixedFlockNeighbor[] neighbors = [new(1, X * FixedQ4816.FromDouble(0.5), Z, One, One)];
        var result = FixedFlockSteering.Evaluate(0, FixedVector3.Zero, FixedVector3.Zero, FixedVector3.Zero, neighbors, All);
        Assert.Equal(-X * FixedQ4816.FromDouble(0.5), result.Separation);
        Assert.Equal(Z, result.Alignment);
        Assert.Equal(X, result.Cohesion);
        var separateOnly = FixedFlockSteering.Evaluate(0, X, X, FixedVector3.Zero, neighbors,
            new FixedFlockWeights(One, One, Zero, Zero, Zero, Zero));
        Assert.True(separateOnly.Desired.X < Zero);
        var none = FixedFlockSteering.Evaluate(0, X, X, FixedVector3.Zero, neighbors, default);
        Assert.Equal(FixedVector3.Zero, none.Desired);
    }

    [Fact]
    public void CompetenceCanInfluenceHeadingWithoutAttraction() {
        FixedFlockNeighbor[] neighbors = [new(1, X, Z, Zero, One)];
        var result = FixedFlockSteering.Evaluate(0, FixedVector3.Zero, FixedVector3.Zero, FixedVector3.Zero, neighbors, All with { Separation = Zero });
        Assert.Equal(FixedVector3.Zero, result.Cohesion);
        Assert.Equal(Z, result.Alignment);
        Assert.Equal(Z, result.Desired);
    }

    [Fact]
    public void CohesionUsesTheCentroidNotMeanUnitDirections() {
        FixedFlockNeighbor[] neighbors = [new(1, X, FixedVector3.Zero, One, Zero), new(2, -X * FixedQ4816.FromInteger(3), FixedVector3.Zero, One, Zero)];
        var result = FixedFlockSteering.Evaluate(0, FixedVector3.Zero, FixedVector3.Zero, FixedVector3.Zero, neighbors, All);
        Assert.Equal(-X, result.Cohesion);
        Array.Reverse(neighbors);
        Assert.Equal(result, FixedFlockSteering.Evaluate(0, FixedVector3.Zero, FixedVector3.Zero, FixedVector3.Zero, neighbors, All));
    }

    [Fact]
    public void CoincidentPairsSeparateAntisymmetricallyInEveryMotionPlane() {
        foreach (var normal in new[] { FixedVector3.Zero, X, Y, Z, X + Y + Z }) {
            for (var index = 0; index < 30; index++) {
                var left = FixedFlockSteering.Evaluate(index, FixedVector3.Zero, FixedVector3.Zero, normal,
                    [new(index + 1, FixedVector3.Zero, FixedVector3.Zero, Zero, Zero)], All);
                var right = FixedFlockSteering.Evaluate(index + 1, FixedVector3.Zero, FixedVector3.Zero, normal,
                    [new(index, FixedVector3.Zero, FixedVector3.Zero, Zero, Zero)], All);
                Assert.NotEqual(FixedVector3.Zero, left.Separation);
                Assert.Equal(-left.Separation, right.Separation);
                Assert.InRange(FixedQ4816.Abs(FixedVector3.Dot(left.Desired, normal)).Value, 0L, 4L);
            }
        }
    }

    [Fact]
    public void GroundedGoalsProjectToTheActualPlaneWhileVolumeGoalsKeepAltitude() {
        var weights = All with { Inertia = Zero };
        Assert.Equal(FixedVector3.Zero, FixedFlockSteering.Evaluate(0, X, Y, Y, [], weights).Desired);
        Assert.Equal(Y, FixedFlockSteering.Evaluate(0, X, Y, FixedVector3.Zero, [], weights).Desired);
        Assert.Equal(FixedVector3.Zero, FixedFlockSteering.Evaluate(0, FixedVector3.Zero, X + Y + Z, X + Y + Z, [], weights).Desired);
        var projected = FixedFlockSteering.Evaluate(0, FixedVector3.Zero, Y + Z, X + Y, [], weights).Desired;
        Assert.InRange(FixedQ4816.Abs(FixedVector3.Dot(projected, X + Y)).Value, 0L, 4L);
        Assert.True(projected.Z > Zero);
    }

    [Fact]
    public void FullWidthCentroidAccumulationIsOrderIndependentAndDoesNotWrap() {
        var extreme = new FixedVector3(FixedQ4816.MaxValue, FixedQ4816.MinValue, Zero);
        var neighbors = Enumerable.Range(1, 4096).Select(index => new FixedFlockNeighbor(index, extreme, extreme, One, One)).ToArray();
        var result = FixedFlockSteering.Evaluate(0, FixedVector3.Zero, FixedVector3.Zero, FixedVector3.Zero, neighbors, All);
        Assert.True(result.Desired.X > Zero);
        Assert.True(result.Desired.Y < Zero);
        Assert.InRange(result.Desired.Length.Value, 65534L, 65538L);
        Array.Reverse(neighbors);
        Assert.Equal(result, FixedFlockSteering.Evaluate(0, FixedVector3.Zero, FixedVector3.Zero, FixedVector3.Zero, neighbors, All));
    }

    [Fact]
    public void InvalidWeightsRefuseAndEmptyInfluenceDoesNotInventMotion() {
        Assert.Equal(FixedVector3.Zero, FixedFlockSteering.Evaluate(0, FixedVector3.Zero, FixedVector3.Zero, Y, [], All).Desired);
        Assert.Throws<ArgumentOutOfRangeException>(() => FixedFlockSteering.Evaluate(0, X, X, Y, [], All with { Cohesion = -One }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FixedFlockSteering.Evaluate(0, X, X, Y,
            [new(1, X, X, FixedQ4816.FromInteger(2), One)], All));
    }
}
