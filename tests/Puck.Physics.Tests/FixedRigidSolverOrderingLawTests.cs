using Puck.Physics.Tests.Fixtures;
using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>The independent state families a digest sensitivity arm perturbs, one raw bit at a time.</summary>
public enum SensitivityChannel {
    /// <summary>The dynamic body's linear velocity.</summary>
    Linear,
    /// <summary>The dynamic body's angular velocity.</summary>
    Angular,
    /// <summary>The dynamic body's orientation.</summary>
    Orientation,
    /// <summary>A persistent manifold slot's accumulated normal impulse.</summary>
    SlotImpulse,
    /// <summary>A persistent manifold slot's source identity.</summary>
    SlotIdentity,
    /// <summary>A persistent manifold slot's last-touched step — the retirement and eviction key.</summary>
    SlotAge,
}
/// <summary>
/// The ordering, warm-start and determinism laws. Every one of them compares two runs of the SAME physical fixture,
/// so a difference can only come from the mechanism the law names.
/// </summary>
public sealed class OrderingLawTests {
    private static readonly int[] PermutationKeys = [0, 1, 7, 100, 5_000, 40_319];

    [Fact]
    public void ShuffledCandidateOrderYieldsBitIdenticalState() {
        var reference = RunPermuted(key: PermutationKeys[0], canonicalOrder: true, steps: 240);

        for (var index = 1; (index < PermutationKeys.Length); ++index) {
            var permuted = RunPermuted(key: PermutationKeys[index], canonicalOrder: true, steps: 240);

            Assert.Equal(actual: permuted, expected: reference);
        }
    }
    [Fact]
    public void WithoutTheCanonicalSortShuffledCandidateOrderDiverges() {
        var reference = RunPermuted(key: PermutationKeys[0], canonicalOrder: false, steps: 240);
        var diverged = false;

        for (var index = 1; (index < PermutationKeys.Length); ++index) {
            diverged |= (RunPermuted(key: PermutationKeys[index], canonicalOrder: false, steps: 240) != reference);
        }

        Assert.True(condition: diverged, userMessage: "removing the canonical sort must let candidate order reach the result");
    }
    [Fact]
    public void WarmStartCollapsesTheFirstIterationResidual() {
        var warm = SettledProfile(warmStart: true);
        var cold = SettledProfile(warmStart: false);

        // The whole point of a warm start is that the first iteration has almost nothing left to do.
        Assert.True(
            condition: ((warm * 4L) < cold),
            userMessage: $"the first biased iteration left {warm} raw units warm-started and {cold} cold; the warm residual must be at least four times smaller"
        );
    }
    [Fact]
    public void WarmStartReducesTheIterationsAMultiContactManifoldNeeds() {
        var warm = SettledIterations(warmStart: true);
        var cold = SettledIterations(warmStart: false);

        Assert.True(
            condition: (warm < cold),
            userMessage: $"the warm-started manifold converged in {warm} iterations and the cold one in {cold}"
        );
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [Theory]
    public void TwoRunsFromIdenticalStateAgreeBitForBitOverAThousandSteps(int fixtureIndex) {
        var first = Build(fixtureIndex: fixtureIndex);
        var second = Build(fixtureIndex: fixtureIndex);

        for (var step = 0; (step < 1_200); ++step) {
            first.Advance();
            second.Advance();

            if (first.Digest != second.Digest) {
                Assert.Fail(message: $"fixture {fixtureIndex} diverged at step {(step + 1)}");
            }
        }

        Assert.Equal(expected: 0, actual: first.Solver.RefusalCount);
        Assert.Equal(expected: 0, actual: second.Solver.RefusalCount);
    }
    [InlineData(SensitivityChannel.Linear)]
    [InlineData(SensitivityChannel.Angular)]
    [InlineData(SensitivityChannel.Orientation)]
    [InlineData(SensitivityChannel.SlotImpulse)]
    [InlineData(SensitivityChannel.SlotIdentity)]
    [InlineData(SensitivityChannel.SlotAge)]
    [Theory]
    public void AOneUnitPerturbationOnEachChannelIsVisibleToTheDigest(SensitivityChannel channel) {
        // The control for the determinism law, per channel: without it, digest equality would be evidence about the
        // instrument's blindness on that channel rather than about the state it claims to fingerprint. The slot
        // channels perturb a persistent manifold slot directly and compare BEFORE any further Advance() — Associate
        // overwrites a slot's identity fields from that step's candidate regardless of match, so a channel that
        // survived a step would no longer be testing what this arm perturbed.
        var reference = Build(fixtureIndex: 0);
        var perturbed = Build(fixtureIndex: 0);

        if (RequiresAnOccupiedSlot(channel: channel)) {
            // Both worlds are built identically, so an identical warm-up settles them bit-for-bit alike.
            reference.Advance(count: 60);
            perturbed.Advance(count: 60);

            Assert.Equal(expected: reference.Digest, actual: perturbed.Digest);
        }

        Perturb(channel: channel, world: perturbed);

        Assert.NotEqual(expected: reference.Digest, actual: perturbed.Digest);
    }

    private static bool RequiresAnOccupiedSlot(SensitivityChannel channel) =>
        ((channel == SensitivityChannel.SlotImpulse) || (channel == SensitivityChannel.SlotIdentity) || (channel == SensitivityChannel.SlotAge));
    private static void Perturb(SpikeWorld world, SensitivityChannel channel) {
        switch (channel) {
            case SensitivityChannel.Linear:
                world.Body.LinearVelocity = new(
                    X: FixedQ4816.FromRawBits(value: (world.Body.LinearVelocity.X.Value + 1L)),
                    Y: world.Body.LinearVelocity.Y,
                    Z: world.Body.LinearVelocity.Z
                );

                break;
            case SensitivityChannel.Angular:
                world.Body.AngularVelocity = new(
                    X: FixedQ4816.FromRawBits(value: (world.Body.AngularVelocity.X.Value + 1L)),
                    Y: world.Body.AngularVelocity.Y,
                    Z: world.Body.AngularVelocity.Z
                );

                break;
            case SensitivityChannel.Orientation:
                world.Body.Orientation = new(
                    X: FixedQ4816.FromRawBits(value: (world.Body.Orientation.X.Value + 1L)),
                    Y: world.Body.Orientation.Y,
                    Z: world.Body.Orientation.Z,
                    W: world.Body.Orientation.W
                );

                break;
            case SensitivityChannel.SlotImpulse:
                FirstOccupiedSlot(world: world).NormalImpulseRaw += 1L;

                break;
            case SensitivityChannel.SlotIdentity:
                FirstOccupiedSlot(world: world).SourceId += 1;

                break;
            case SensitivityChannel.SlotAge:
                FirstOccupiedSlot(world: world).LastTouchedStep -= 1;

                break;
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(channel));
        }
    }
    private static ref FixedManifoldSlot FirstOccupiedSlot(SpikeWorld world) {
        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            if (world.Slots[index].Occupied) {
                return ref world.Slots[index];
            }
        }

        throw new InvalidOperationException(message: "no occupied slot to perturb");
    }
    private static SpikeWorld Build(int fixtureIndex) =>
        fixtureIndex switch {
            0 => SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(rateHz: 60, substepCount: 4)),
            1 => SpikeFixtures.CapsuleWaist(options: new() { RateHz = 60, SubstepCount = 4, }, mode: Geometry.CapsuleWitnessMode.SegmentScan, surface: out _),
            2 => SpikeFixtures.RotatingBox(options: new() { RateHz = 60, SubstepCount = 4, }),
            3 => SpikeFixtures.HighSpeedApproach(options: new() { RateHz = 60, SubstepCount = 1, }, height: 1d, downwardSpeed: 400d),
            4 => SpikeFixtures.DeepOverlap(options: new() { RateHz = 60, SubstepCount = 4, }),
            _ => SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(rateHz: 60, substepCount: 4)),
        };
    private static ulong RunPermuted(int key, bool canonicalOrder, int steps) {
        var world = SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(rateHz: 60, substepCount: 4, canonicalOrder: canonicalOrder));

        world.Permutation = candidates => Permute(key: key, source: candidates);
        world.Advance(count: steps);

        Assert.Equal(expected: 0, actual: world.Solver.RefusalCount);

        return world.Digest;
    }
    // The key's Lehmer decoding, so a permutation index names one permutation and the test scaffolding carries no
    // generator state of its own.
    private static List<FixedContactCandidate> Permute(List<FixedContactCandidate> source, int key) {
        var pool = new List<FixedContactCandidate>(collection: source);
        var result = new List<FixedContactCandidate>(capacity: pool.Count);
        var remainder = key;

        while (pool.Count > 0) {
            var pick = (remainder % pool.Count);

            remainder /= pool.Count;
            result.Add(item: pool[pick]);
            pool.RemoveAt(index: pick);
        }

        return result;
    }
    private static long SettledProfile(bool warmStart) {
        var world = SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(rateHz: 60, substepCount: 4, solveIterations: 16, warmStart: warmStart));

        world.Advance(count: 240);

        return world.Solver.IterationProfile[0];
    }
    private static int SettledIterations(bool warmStart) {
        var world = SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(rateHz: 60, substepCount: 4, solveIterations: 16, warmStart: warmStart));

        world.Advance(count: 240);

        return world.Solver.IterationsToConverge(toleranceRaw: 64L);
    }
}
