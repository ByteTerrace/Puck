using System.Globalization;

using Puck.Dynamics.Spike.Tests.Core;
using Puck.Dynamics.Spike.Tests.Fixtures;
using Puck.Dynamics.Spike.Tests.Geometry;
using Puck.Maths;

using Xunit;

namespace Puck.Dynamics.Spike.Tests;

/// <summary>
/// The numbers the spike was asked to report rather than guess: the softness coefficients at every rate and substep
/// count, the iteration budget each fixture actually needs, and the per-step field-sample budget.
/// </summary>
public sealed class MeasurementTests {
    private static readonly int[] Rates = [30, 60, 120];
    private static readonly int[] SubstepCounts = [1, 4, 8];

    [Fact]
    public void SoftnessCoefficientsAreRecordedAcrossEveryRateAndSubstepCount() {
        SpikeReport.Section(title: "Softness coefficients (formed at substep h)");
        SpikeReport.Write(line: "rateHz | n | authoredHertz | clampedHertz | biasRate | massScale | impulseScale | rawBias | rawMass");

        var authored = FixedQ4816.FromInteger(value: 30L);
        var damping = FixedQ4816.FromInteger(value: 10L);

        foreach (var rate in Rates) {
            foreach (var substeps in SubstepCounts) {
                var softness = SoftConstraint.Create(
                    rateHz: rate,
                    substepCount: substeps,
                    hertz: authored,
                    dampingRatio: damping,
                    fractionBitCount: SoftConstraint.DefaultFractionBitCount
                );

                Assert.Equal(expected: (1L << SoftConstraint.DefaultFractionBitCount), actual: (softness.MassScaleRaw + softness.ImpulseScaleRaw));
                SpikeReport.Write(line: string.Join(
                    separator: " | ",
                    values: [
                        rate.ToString(provider: CultureInfo.InvariantCulture),
                        substeps.ToString(provider: CultureInfo.InvariantCulture),
                        SpikeReport.Format(value: authored),
                        SpikeReport.Format(value: FixedQ4816.FromRawBits(value: softness.ClampedHertzRaw)),
                        Scaled(raw: softness.BiasRateRaw),
                        Scaled(raw: softness.MassScaleRaw),
                        Scaled(raw: softness.ImpulseScaleRaw),
                        softness.BiasRateRaw.ToString(provider: CultureInfo.InvariantCulture),
                        softness.MassScaleRaw.ToString(provider: CultureInfo.InvariantCulture),
                    ]
                ));
            }
        }
    }

    [Fact]
    public void TheClampCeilingRefusesAnEffectiveSubstepRateThatWouldOverflowItsShift() {
        const int RateAtBoundary = (1 << 24);
        const int SubstepsAtBoundary = (1 << 23); // rateHz · substepCount == 2^47, the shift's exact overflow boundary

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SoftConstraint.Create(
            rateHz: RateAtBoundary,
            substepCount: SubstepsAtBoundary,
            hertz: FixedQ4816.FromInteger(value: 30L),
            dampingRatio: FixedQ4816.FromInteger(value: 10L),
            fractionBitCount: SoftConstraint.DefaultFractionBitCount
        ));

        var underBoundary = SoftConstraint.Create(
            rateHz: RateAtBoundary,
            substepCount: (SubstepsAtBoundary - 1),
            hertz: FixedQ4816.FromInteger(value: 30L),
            dampingRatio: FixedQ4816.FromInteger(value: 10L),
            fractionBitCount: SoftConstraint.DefaultFractionBitCount
        );

        Assert.Equal(expected: (1L << SoftConstraint.DefaultFractionBitCount), actual: (underBoundary.MassScaleRaw + underBoundary.ImpulseScaleRaw));
    }

    [Fact]
    public void IterationsToConvergeAreRecordedAcrossEveryRateAndSubstepCount() {
        SpikeReport.Section(title: "Iterations to converge (budget 16, whole budget always run)");
        SpikeReport.Write(line: "fixture | rateHz | n | warmStart | iterationsAt64 | iterationsAt512 | settledProfile(first 6, raw Q48.16)");

        foreach (var rate in Rates) {
            foreach (var substeps in SubstepCounts) {
                foreach (var warmStart in new[] { true, false, }) {
                    Measure(
                        name: "corner",
                        world: SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(rateHz: rate, substepCount: substeps, solveIterations: 16, warmStart: warmStart)),
                        rate: rate,
                        substeps: substeps,
                        warmStart: warmStart
                    );
                    Measure(
                        name: "boxInCorner",
                        world: SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(rateHz: rate, substepCount: substeps, solveIterations: 16, warmStart: warmStart)),
                        rate: rate,
                        substeps: substeps,
                        warmStart: warmStart
                    );
                }
            }
        }
    }

    [Fact]
    public void FieldSampleBudgetIsRecordedPerStep() {
        SpikeReport.Section(title: "Field samples per step (capsule waist, standalone SdfProgram)");
        SpikeReport.Write(line: "mode | phase | samplesPerStep | candidates | endpointSeparation | witnessSeparation");

        foreach (var mode in new[] { CapsuleWitnessMode.SegmentScan, CapsuleWitnessMode.EndpointsOnly, }) {
            var world = SpikeFixtures.CapsuleWaist(options: new() { RateHz = 60, SubstepCount = 4, }, mode: mode, surface: out var surface);
            var approachSamples = 0;
            var approachCandidates = 0;

            world.Advance();
            approachSamples = world.LastStepSampleCount;
            approachCandidates = world.LastStepCandidateCount;
            SpikeReport.Write(line: $"{mode} | approach | {approachSamples} | {approachCandidates} | {SpikeReport.Format(value: surface.LastEndpointSeparation)} | {SpikeReport.Format(value: surface.LastWitnessSeparation)}");
            world.Advance(count: 119);
            SpikeReport.Write(line: $"{mode} | settled | {world.LastStepSampleCount} | {world.LastStepCandidateCount} | {SpikeReport.Format(value: surface.LastEndpointSeparation)} | {SpikeReport.Format(value: surface.LastWitnessSeparation)}");
            Assert.Equal(expected: 0, actual: world.Solver.RefusalCount);
        }
    }

    [Fact]
    public void EffectiveMassAndImpulseRangesAreRecorded() {
        SpikeReport.Section(title: "Observed solver quantity ranges (raw carriers)");
        SpikeReport.Write(line: "fixture | minimumNormalMassRaw | maximumImpulseRaw | inverseMassRaw | inverseInertiaXXRaw");

        RecordRanges(name: "corner", world: SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(rateHz: 60, substepCount: 4)), steps: 240);
        RecordRanges(name: "rotatingBox", world: SpikeFixtures.RotatingBox(options: new() { RateHz = 60, SubstepCount = 4, }), steps: 240);
        RecordRanges(name: "boxInCorner", world: SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(rateHz: 60, substepCount: 4)), steps: 240);
    }

    [Fact]
    public void SabotageOutcomesAreRecorded() {
        SpikeReport.Section(title: "Sabotage outcomes (the observation each red run leaves)");
        SpikeReport.Write(line: "law | mechanism | intended | sabotaged");

        var corner = SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(rateHz: 60, substepCount: 4));
        var aliased = SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(rateHz: 60, substepCount: 4, compositeIdentity: false));

        corner.Advance(count: 240);
        aliased.Advance(count: 240);
        SpikeReport.Write(line: $"corner non-aliasing | composite identity | slots={corner.Slots.ActiveCount} y={SpikeReport.Format(value: corner.Pose.Center.Y)} | slots={aliased.Slots.ActiveCount} y={SpikeReport.Format(value: aliased.Pose.Center.Y)}");

        var scanned = SpikeFixtures.CapsuleWaist(options: new() { RateHz = 60, SubstepCount = 4, }, mode: CapsuleWitnessMode.SegmentScan, surface: out _);
        var endpoints = SpikeFixtures.CapsuleWaist(options: new() { RateHz = 60, SubstepCount = 4, }, mode: CapsuleWitnessMode.EndpointsOnly, surface: out _);

        scanned.Advance(count: 120);
        endpoints.Advance(count: 120);
        SpikeReport.Write(line: $"capsule waist | witness search | y={SpikeReport.Format(value: scanned.Pose.Center.Y)} | y={SpikeReport.Format(value: endpoints.Pose.Center.Y)}");

        var caught = SpikeFixtures.HighSpeedApproach(options: new() { RateHz = 60, SubstepCount = 1, }, height: 1d, downwardSpeed: 400d);
        var tunnelled = SpikeFixtures.HighSpeedApproach(options: new() { RateHz = 60, SubstepCount = 1, Activation = SpeculativeActivation.CurrentOnly, }, height: 1d, downwardSpeed: 400d);
        var nearest = SpikeFixtures.HighSpeedApproach(options: new() { RateHz = 60, SubstepCount = 1, Activation = SpeculativeActivation.NearestRounded, }, height: 1d, downwardSpeed: 400d);

        caught.Advance();
        tunnelled.Advance();
        nearest.Advance();
        SpikeReport.Write(line: $"speculative no-tunnel | swept activation bound | bound={SpikeReport.Format(value: caught.LastStepActivationBound)} y={SpikeReport.Format(value: caught.Pose.Center.Y)} | bound={SpikeReport.Format(value: tunnelled.LastStepActivationBound)} y={SpikeReport.Format(value: tunnelled.Pose.Center.Y)}");
        SpikeReport.Write(line: $"speculative rounding direction | ceiling vs nearest | ceilingBound={SpikeReport.Format(value: caught.LastStepActivationBound)} | nearestBound={SpikeReport.Format(value: nearest.LastStepActivationBound)} deltaRaw={(caught.LastStepActivationBound.Value - nearest.LastStepActivationBound.Value)}");

        var recovered = SpikeFixtures.DeepOverlap(options: new() { RateHz = 60, SubstepCount = 4, });
        var expelled = SpikeFixtures.DeepOverlap(options: new() { RateHz = 60, SubstepCount = 4, DeepRecovery = false, });

        recovered.Advance(count: 60);
        expelled.Advance(count: 60);
        SpikeReport.Write(line: $"deep recovery | bounded extraction | y={SpikeReport.Format(value: recovered.Pose.Center.Y)} | y={SpikeReport.Format(value: expelled.Pose.Center.Y)}");
        SpikeReport.Write(line: $"canonical order | permuted digests | {PermutationDigests(canonicalOrder: true)} | {PermutationDigests(canonicalOrder: false)}");
    }

    private static string PermutationDigests(bool canonicalOrder) {
        var keys = new[] { 0, 1, 7, 100, };
        var digests = new string[keys.Length];

        for (var index = 0; (index < keys.Length); ++index) {
            var world = SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(rateHz: 60, substepCount: 4, canonicalOrder: canonicalOrder));
            var key = keys[index];

            world.Permutation = candidates => Reorder(source: candidates, key: key);
            world.Advance(count: 240);
            digests[index] = world.Digest.ToString(format: "X16", provider: CultureInfo.InvariantCulture);
        }

        return string.Join(separator: ",", value: digests);
    }

    private static List<ContactCandidate> Reorder(List<ContactCandidate> source, int key) {
        var pool = new List<ContactCandidate>(collection: source);
        var result = new List<ContactCandidate>(capacity: pool.Count);
        var remainder = key;

        while (pool.Count > 0) {
            var pick = (remainder % pool.Count);

            remainder /= pool.Count;
            result.Add(item: pool[pick]);
            pool.RemoveAt(index: pick);
        }

        return result;
    }

    private static void RecordRanges(string name, SpikeWorld world, int steps) {
        var minimumNormalMass = long.MaxValue;
        var maximumImpulse = 0L;

        for (var step = 0; (step < steps); ++step) {
            world.Advance();

            if ((world.Solver.LastStepMinimumNormalMassRaw > 0L) && (world.Solver.LastStepMinimumNormalMassRaw < minimumNormalMass)) {
                minimumNormalMass = world.Solver.LastStepMinimumNormalMassRaw;
            }

            if (world.Solver.LastStepMaximumImpulseRaw > maximumImpulse) {
                maximumImpulse = world.Solver.LastStepMaximumImpulseRaw;
            }
        }

        Assert.Equal(expected: 0, actual: world.Solver.RefusalCount);
        SpikeReport.Write(line: $"{name} | {minimumNormalMass} | {maximumImpulse} | {world.Body.InverseMassRaw} | {world.Body.InverseInertiaXX}");
    }

    private static void Measure(string name, SpikeWorld world, int rate, int substeps, bool warmStart) {
        world.Advance(count: (4 * rate));
        Assert.Equal(expected: 0, actual: world.Solver.RefusalCount);
        SpikeReport.Write(line: $"{name} | {rate} | {substeps} | {warmStart} | {world.Solver.IterationsToConverge(toleranceRaw: 64L)} | {world.Solver.IterationsToConverge(toleranceRaw: 512L)} | {Profile(solver: world.Solver)}");
    }

    private static string Profile(RigidSolver solver) {
        var profile = solver.IterationProfile;
        var parts = new string[Math.Min(val1: 6, val2: profile.Length)];

        for (var index = 0; (index < parts.Length); ++index) {
            parts[index] = profile[index].ToString(provider: CultureInfo.InvariantCulture);
        }

        return string.Join(separator: ",", value: parts);
    }

    private static string Scaled(long raw) =>
        (((double)raw) / (1L << SoftConstraint.DefaultFractionBitCount)).ToString(format: "0.######", provider: CultureInfo.InvariantCulture);
}
