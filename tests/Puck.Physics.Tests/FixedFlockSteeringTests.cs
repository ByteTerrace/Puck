using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedFlockSteeringTests {
    private static readonly FixedQ4816 One = FixedQ4816.One;
    private static readonly FixedQ4816 Zero = FixedQ4816.Zero;
    private static readonly FixedVector3 X = new(
        X: One,
        Y: Zero,
        Z: Zero
    );
    private static readonly FixedVector3 Y = new(
        X: Zero,
        Y: One,
        Z: Zero
    );
    private static readonly FixedVector3 Z = new(
        X: Zero,
        Y: Zero,
        Z: One
    );
    private static readonly FixedFlockWeights All = new(
        Alignment: One,
        Cohesion: One,
        Goal: One,
        Inertia: One,
        Separation: One,
        SeparationRadius: One
    );

    [Fact]
    public void CohesionUsesTheCentroidNotMeanUnitDirections() {
        FixedFlockNeighbor[] neighbors = [new(
                1,
                X,
                FixedVector3.Zero,
                One,
                Zero
            ), new(
                2,
                (-X * FixedQ4816.FromInteger(value: 3)),
                FixedVector3.Zero,
                One,
                Zero
            )];
        var result = FixedFlockSteering.Evaluate(
            0,
            FixedVector3.Zero,
            FixedVector3.Zero,
            FixedVector3.Zero,
            neighbors,
            All
        );

        Assert.Equal(
            -X,
            result.Cohesion
        );
        Array.Reverse(array: neighbors);
        Assert.Equal(
            result,
            FixedFlockSteering.Evaluate(
                0,
                FixedVector3.Zero,
                FixedVector3.Zero,
                FixedVector3.Zero,
                neighbors,
                All
            )
        );
    }
    [Fact]
    public void CoincidentPairsSeparateAntisymmetricallyInEveryMotionPlane() {
        foreach (var normal in new[] { FixedVector3.Zero, X, Y, Z, ((X + Y) + Z) }) {
            for (var index = 0; (index < 30); index++) {
                var left = FixedFlockSteering.Evaluate(
                    index,
                    FixedVector3.Zero,
                    FixedVector3.Zero,
                    normal,
                    [new(
                            (index + 1),
                            FixedVector3.Zero,
                            FixedVector3.Zero,
                            Zero,
                            Zero
                        )],
                    All
                );
                var right = FixedFlockSteering.Evaluate(
                    (index + 1),
                    FixedVector3.Zero,
                    FixedVector3.Zero,
                    normal,
                    [new(
                            index,
                            FixedVector3.Zero,
                            FixedVector3.Zero,
                            Zero,
                            Zero
                        )],
                    All
                );

                Assert.NotEqual(
                    FixedVector3.Zero,
                    left.Separation
                );
                Assert.Equal(
                    -left.Separation,
                    right.Separation
                );
                Assert.InRange(
                    FixedQ4816.Abs(value: FixedVector3.Dot(
                        left: left.Desired,
                        right: normal
                    )).Value,
                    0L,
                    4L
                );
            }
        }
    }
    [Fact]
    public void CompetenceCanInfluenceHeadingWithoutAttraction() {
        FixedFlockNeighbor[] neighbors = [new(
                AlignmentAffinity: One,
                CohesionAffinity: Zero,
                Index: 1,
                Offset: X,
                Velocity: Z
            )];
        var result = FixedFlockSteering.Evaluate(
            0,
            FixedVector3.Zero,
            FixedVector3.Zero,
            FixedVector3.Zero,
            neighbors,
            All with { Separation = Zero }
        );

        Assert.Equal(
            FixedVector3.Zero,
            result.Cohesion
        );
        Assert.Equal(
            Z,
            result.Alignment
        );
        Assert.Equal(
            Z,
            result.Desired
        );
    }
    [Fact]
    public void FullWidthCentroidAccumulationIsOrderIndependentAndDoesNotWrap() {
        var extreme = new FixedVector3(
            X: FixedQ4816.MaxValue,
            Y: FixedQ4816.MinValue,
            Z: Zero
        );
        var neighbors = Enumerable.Range(
            count: 4096,
            start: 1
        ).Select(selector: index => new FixedFlockNeighbor(
            AlignmentAffinity: One,
            CohesionAffinity: One,
            Index: index,
            Offset: extreme,
            Velocity: extreme
        )).ToArray();
        var result = FixedFlockSteering.Evaluate(
            0,
            FixedVector3.Zero,
            FixedVector3.Zero,
            FixedVector3.Zero,
            neighbors,
            All
        );

        Assert.True(condition: (result.Desired.X > Zero));
        Assert.True(condition: (result.Desired.Y < Zero));
        Assert.InRange(
            result.Desired.Length.Value,
            65534L,
            65538L
        );
        Array.Reverse(array: neighbors);
        Assert.Equal(
            result,
            FixedFlockSteering.Evaluate(
                0,
                FixedVector3.Zero,
                FixedVector3.Zero,
                FixedVector3.Zero,
                neighbors,
                All
            )
        );
    }
    [Fact]
    public void GroundedGoalsProjectToTheActualPlaneWhileVolumeGoalsKeepAltitude() {
        var weights = All with { Inertia = Zero };

        Assert.Equal(
            FixedVector3.Zero,
            FixedFlockSteering.Evaluate(
                goalDirection: Y,
                neighbors: [],
                planeNormal: Y,
                selfIndex: 0,
                velocity: X,
                weights: weights
            ).Desired
        );
        Assert.Equal(
            Y,
            FixedFlockSteering.Evaluate(
                0,
                X,
                Y,
                FixedVector3.Zero,
                [],
                weights
            ).Desired
        );
        Assert.Equal(
            FixedVector3.Zero,
            FixedFlockSteering.Evaluate(
                0,
                FixedVector3.Zero,
                ((X + Y) + Z),
                ((X + Y) + Z),
                [],
                weights
            ).Desired
        );
        var projected = FixedFlockSteering.Evaluate(
            0,
            FixedVector3.Zero,
            (Y + Z),
            (X + Y),
            [],
            weights
        ).Desired;

        Assert.InRange(
            FixedQ4816.Abs(value: FixedVector3.Dot(
                left: projected,
                right: (X + Y)
            )).Value,
            0L,
            4L
        );
        Assert.True(condition: (projected.Z > Zero));
    }
    [Fact]
    public void IndividualTermsHaveIndependentDirectionalWitnesses() {
        FixedFlockNeighbor[] neighbors = [new(
                1,
                (X * FixedQ4816.FromDouble(value: 0.5)),
                Z,
                One,
                One
            )];
        var result = FixedFlockSteering.Evaluate(
            0,
            FixedVector3.Zero,
            FixedVector3.Zero,
            FixedVector3.Zero,
            neighbors,
            All
        );

        Assert.Equal(
            (-X * FixedQ4816.FromDouble(value: 0.5)),
            result.Separation
        );
        Assert.Equal(
            Z,
            result.Alignment
        );
        Assert.Equal(
            X,
            result.Cohesion
        );
        var separateOnly = FixedFlockSteering.Evaluate(
            0,
            X,
            X,
            FixedVector3.Zero,
            neighbors,
            new FixedFlockWeights(
                Alignment: Zero,
                Cohesion: Zero,
                Goal: Zero,
                Inertia: Zero,
                Separation: One,
                SeparationRadius: One
            )
        );

        Assert.True(condition: (separateOnly.Desired.X < Zero));
        var none = FixedFlockSteering.Evaluate(
            0,
            X,
            X,
            FixedVector3.Zero,
            neighbors,
            default
        );

        Assert.Equal(
            FixedVector3.Zero,
            none.Desired
        );
    }
    [Fact]
    public void InvalidWeightsRefuseAndEmptyInfluenceDoesNotInventMotion() {
        Assert.Equal(
            FixedVector3.Zero,
            FixedFlockSteering.Evaluate(
                0,
                FixedVector3.Zero,
                FixedVector3.Zero,
                Y,
                [],
                All
            ).Desired
        );
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => FixedFlockSteering.Evaluate(
            0,
            X,
            X,
            Y,
            [],
            All with { Cohesion = -One }
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => FixedFlockSteering.Evaluate(
            0,
            X,
            X,
            Y,
            [new(
                    1,
                    X,
                    X,
                    FixedQ4816.FromInteger(value: 2),
                    One
                )],
            All
        ));
    }
}
