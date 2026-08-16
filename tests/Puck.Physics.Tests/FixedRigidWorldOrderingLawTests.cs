using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// Canonical-ordering laws for <see cref="FixedRigidWorld"/>: candidate/pair-generation order permutation invariance
/// at FIXED body ids, and the narrower relabelling invariance that actually holds for a Gauss-Seidel sequential
/// solve — swapping which body of a SINGLE pair got the lower id, never a general N-body insertion-order claim.
/// </summary>
public sealed class FixedRigidWorldOrderingLawTests {
    private static FixedRigidBody MakeBody(long inverseMass) =>
        new() { InverseInertiaXX = inverseMass, InverseInertiaYY = inverseMass, InverseInertiaZZ = inverseMass, InverseMassRaw = inverseMass, };
    private static List<FixedTwoBodyContact> Permute(List<FixedTwoBodyContact> source, int key) {
        var pool = new List<FixedTwoBodyContact>(collection: source);
        var result = new List<FixedTwoBodyContact>(capacity: pool.Count);
        var remainder = key;

        while (pool.Count > 0) {
            var pick = (remainder % pool.Count);

            remainder /= pool.Count;
            result.Add(item: pool[pick]);
            pool.RemoveAt(index: pick);
        }

        return result;
    }
    private static BodySnapshot[] RunThreeBodyChain(int permutationKey) {
        var options = new FixedRigidSolverOptions { RateHz = 60, SubstepCount = 4, };
        var world = new FixedRigidWorld(options: options);
        var bodies = new[] { MakeBody(inverseMass: 40_000_000_000L), MakeBody(inverseMass: 20_000_000_000L), MakeBody(inverseMass: 10_000_000_000L), };
        var ids = new int[3];

        for (var index = 0; (index < 3); ++index) {
            ids[index] = world.AddBody(body: bodies[index]);
        }

        bodies[0].LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: -2d),
            Z: FixedQ4816.Zero
        );
        bodies[2].LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: 1.5d),
            Z: FixedQ4816.Zero
        );

        var normal = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.One,
            Z: FixedQ4816.Zero
        );
        var anchorUp = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: 0.5d),
            Z: FixedQ4816.Zero
        );
        var anchorDown = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: -0.5d),
            Z: FixedQ4816.Zero
        );
        var full = new List<FixedTwoBodyContact> {
            new(
            BodyIdA: ids[0],
            BodyIdB: ids[1],
            AnchorA: anchorUp,
            AnchorB: anchorDown,
            Normal: normal,
            Separation: FixedQ4816.Zero,
            SourceId: 1,
            FeatureId: 0
        ),
            new(
            BodyIdA: ids[1],
            BodyIdB: ids[2],
            AnchorA: anchorUp,
            AnchorB: anchorDown,
            Normal: normal,
            Separation: FixedQ4816.Zero,
            SourceId: 2,
            FeatureId: 0
        ),
        };

        for (var step = 1; (step <= 40); ++step) {
            world.Step(
                candidates: Permute(
                    key: permutationKey,
                    source: full
                ),
                step: step
            );
        }

        Assert.Equal(
            expected: 0,
            actual: world.RefusalCount
        );

        var snapshot = new BodySnapshot[bodies.Length];

        for (var index = 0; (index < bodies.Length); ++index) {
            snapshot[index] = new(
                LinearVelocity: bodies[index].LinearVelocity,
                AngularVelocity: bodies[index].AngularVelocity,
                Orientation: bodies[index].Orientation
            );
        }

        return snapshot;
    }

    [Fact]
    public void ATombstonedBodyIdIsNeverReusedAndOtherIdsNeverMove() {
        var world = new FixedRigidWorld(options: new() { RateHz = 60, SubstepCount = 4, });
        var first = world.AddBody(body: MakeBody(inverseMass: 1_000_000_000L));
        var second = world.AddBody(body: MakeBody(inverseMass: 1_000_000_000L));

        world.RemoveBody(id: first);

        var third = world.AddBody(body: MakeBody(inverseMass: 1_000_000_000L));

        Assert.NotEqual(
            actual: third,
            expected: first
        );
        Assert.Equal(
            actual: third,
            expected: 2
        );
        Assert.Null(@object: world.GetBody(id: first));
        Assert.NotNull(@object: world.GetBody(id: second));
        Assert.NotNull(@object: world.GetBody(id: third));
    }
    [Fact]
    public void RelabellingWhichBodyOfASinglePairIsAReproducesTheMirroredResultBitForBit() {
        var options = new FixedRigidSolverOptions { AppliedAcceleration = FixedVector3.Zero, Gravity = FixedVector3.Zero, RateHz = 60, SubstepCount = 4, };
        var worldForward = new FixedRigidWorld(options: options);
        var lowA = MakeBody(inverseMass: 40_000_000_000L);
        var highB = MakeBody(inverseMass: 4_000_000_000L);
        var idLowA = worldForward.AddBody(body: lowA);
        var idHighB = worldForward.AddBody(body: highB);

        lowA.LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: 2d),
            Z: FixedQ4816.Zero
        );
        highB.LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: -1d),
            Z: FixedQ4816.Zero
        );

        var anchorLow = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: 0.5d),
            Z: FixedQ4816.Zero
        );
        var anchorHigh = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: -0.5d),
            Z: FixedQ4816.Zero
        );
        var normal = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.One,
            Z: FixedQ4816.Zero
        );

        for (var step = 1; (step <= 30); ++step) {
            worldForward.Step(
                candidates: [
                    new(
                        BodyIdA: idLowA,
                        BodyIdB: idHighB,
                        AnchorA: anchorLow,
                        AnchorB: anchorHigh,
                        Normal: normal,
                        Separation: FixedQ4816.Zero,
                        SourceId: 1,
                        FeatureId: 0
                    ),
                ],
                step: step
            );
        }

        // The mirror: build the SAME two bodies (by their own physical properties, not by which id they got), but
        // add them to a fresh world in the OPPOSITE order, so the body with the SAME properties as `lowA` now gets
        // the higher id. The candidate is authored with A/B swapped and the normal negated to preserve its physical
        // meaning (pointing from the low-id body toward the high-id body).
        var options2 = new FixedRigidSolverOptions { AppliedAcceleration = FixedVector3.Zero, Gravity = FixedVector3.Zero, RateHz = 60, SubstepCount = 4, };
        var worldMirrored = new FixedRigidWorld(options: options2);
        var highBMirror = MakeBody(inverseMass: 4_000_000_000L);
        var lowAMirror = MakeBody(inverseMass: 40_000_000_000L);
        var idHighBMirror = worldMirrored.AddBody(body: highBMirror);
        var idLowAMirror = worldMirrored.AddBody(body: lowAMirror);

        lowAMirror.LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: 2d),
            Z: FixedQ4816.Zero
        );
        highBMirror.LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: -1d),
            Z: FixedQ4816.Zero
        );

        for (var step = 1; (step <= 30); ++step) {
            worldMirrored.Step(
                candidates: [
                    new(
                        BodyIdA: idHighBMirror,
                        BodyIdB: idLowAMirror,
                        AnchorA: anchorHigh,
                        AnchorB: anchorLow,
                        Normal: -normal,
                        Separation: FixedQ4816.Zero,
                        SourceId: 1,
                        FeatureId: 0
                    ),
                ],
                step: step
            );
        }

        Assert.Equal(
            expected: 0,
            actual: worldForward.RefusalCount
        );
        Assert.Equal(
            expected: 0,
            actual: worldMirrored.RefusalCount
        );
        Assert.Equal(
            expected: lowA.LinearVelocity,
            actual: lowAMirror.LinearVelocity
        );
        Assert.Equal(
            expected: highB.LinearVelocity,
            actual: highBMirror.LinearVelocity
        );
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(5_000)]
    [Theory]
    public void ShuffledCandidateOrderYieldsBitIdenticalStateAtFixedBodyIds(int permutationKey) {
        var reference = RunThreeBodyChain(permutationKey: 0);
        var permuted = RunThreeBodyChain(permutationKey: permutationKey);

        Assert.Equal(
            actual: permuted,
            expected: reference
        );
    }

    private readonly record struct BodySnapshot(FixedVector3 LinearVelocity, FixedVector3 AngularVelocity, FixedQuaternion Orientation);
}
