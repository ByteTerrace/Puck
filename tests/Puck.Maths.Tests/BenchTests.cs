using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// The bench tier — timing facts with breach-tolerant failure semantics. Opt in with
/// <c>dotnet test --settings tests/Puck.Maths.Tests/bench.runsettings</c>. The
/// seeded bench measures the RATIO of the generic <see cref="QuadraticAlgebra{TScalar}"/> multiply to the hand-written
/// <see cref="FixedComplex"/> multiply (narrow regime), compares it to the committed per-machine baseline, and treats
/// noise conservatively: a first breach is informational and auto-reruns; only two consecutive confirmed breaches fail.
/// A machine with no baseline records one from these runs; a busy machine (calibration off by more than 2×) is skipped.
/// </summary>
public sealed class BenchTests {
    /// <summary>The seeded bench id, shared with the coverage registry.</summary>
    public const string RatioBenchId = "bench.complex-mul-ratio";

    private const long Iterations = 5_000_000L;
    // Baseline recording and live comparison MUST use the same sample count so the two ratios measure the same JIT
    // regime: the generic-vs-hand ratio drifts within a process as tiered compilation promotes the loops (empirically
    // ~0.95 for the first samples of a fresh process, climbing past ~1.9 once the generic loop tiers up). A small count
    // keeps both the recording and each compare's first measurement in that early, steady, fast regime, so they are
    // comparable; a larger count would straddle the tiering transition and make the ratio bimodal and flaky.
    private const int RatioSamples = 3;
    // The MAD term of the noise band is trusted only once the baseline holds at least this many runs; below it the band
    // is the five-percent floor alone, so a thin baseline (whose MAD is a fragile estimate from few points) can never
    // contribute a spuriously narrow or wide MAD band. At the current RatioSamples the floor governs by design.
    private const int MinimumRunsForMadTrust = 5;

    [Fact]
    [Trait(name: "tier", value: "Bench")]
    public void ComplexMultiplyRatioWithinBaseline() {
        var fingerprint = Bench.Fingerprint();
        var calibration = Bench.Calibrate();
        var path = TestPaths.Artifact(fileName: "bench-baselines.json");
        var model = (ArtifactJson.ReadOrDefault<BaselineModel>(path: path) ?? new BaselineModel());
        var machine = model.Machines.Find(match: candidate => (candidate.Fingerprint == fingerprint));
        var entry = machine?.Benches.Find(match: candidate => (candidate.Id == RatioBenchId));

        // The environment guard runs before recording as well as before comparing, so a busy machine can never write a
        // baseline it would then be held to. It needs a recorded calibration, which only an existing machine has.
        if ((machine is not null) && ((calibration > (machine.CalibrationNs * 2.0)) || (calibration < (machine.CalibrationNs * 0.5)))) {
            BenchState.Record(id: RatioBenchId, median: Bench.Median(values: MeasureRatios(samples: RatioSamples)), baselineMedian: machine.CalibrationNs, band: 0.0, status: "environment-suspect");
            Assert.Skip(reason: $"environment-suspect: calibration {calibration:F1} ns vs baseline {machine.CalibrationNs:F1} ns (busy machine); not recording or failing.");

            return;
        }

        if (entry is null) {
            // No baseline for this bench on this machine — a fresh machine, or an existing one that has never recorded
            // THIS bench (a newly added or renamed bench id). Record one from the same early, steady sample the compare
            // path uses; an existing machine keeps the calibration it was fingerprinted with.
            var baselineRatios = MeasureRatios(samples: RatioSamples);
            var baselineMedian = Bench.Median(values: baselineRatios);

            if (machine is null) {
                machine = new MachineBaseline { CalibrationNs = calibration, Fingerprint = fingerprint };

                model.Machines.Add(item: machine);
                model.Machines.Sort(comparison: static (left, right) => string.CompareOrdinal(strA: left.Fingerprint, strB: right.Fingerprint));
            }

            machine.Benches.Add(item: new BenchEntry { Id = RatioBenchId, Median = baselineMedian, Runs = baselineRatios, Mad = Bench.MedianAbsoluteDeviation(values: baselineRatios) });
            machine.Benches.Sort(comparison: static (left, right) => string.CompareOrdinal(strA: left.Id, strB: right.Id));

            _ = ArtifactJson.WriteIfChanged(path: path, content: ArtifactJson.Serialize(value: model));
            BenchState.Record(band: 0.0, baselineMedian: baselineMedian, id: RatioBenchId, median: baselineMedian, status: "baseline-recorded");

            return;
        }

        var median = Bench.Median(values: MeasureRatios(samples: RatioSamples));

        // The five-percent floor always holds; the MAD term joins it only once the baseline sample is deep enough to
        // trust (see MinimumRunsForMadTrust), so a thin baseline widens to the floor rather than a fragile MAD estimate.
        var madBand = ((entry.Runs.Count >= MinimumRunsForMadTrust) ? (3.0 * entry.Mad) : 0.0);
        var band = Math.Max(val1: (0.05 * entry.Median), val2: madBand);

        if (Breach(value: median, baseline: entry.Median, band: band)) {
            var rerun = Bench.Median(values: MeasureRatios(samples: RatioSamples));

            if (Breach(value: rerun, baseline: entry.Median, band: band)) {
                BenchState.Record(id: RatioBenchId, median: rerun, baselineMedian: entry.Median, band: band, status: "BREACH-CONFIRMED");
                Assert.Fail(message: $"two consecutive confirmed breaches: {median:F3}, {rerun:F3} vs baseline {entry.Median:F3} ± {band:F3}");
            }

            BenchState.Record(id: RatioBenchId, median: rerun, baselineMedian: entry.Median, band: band, status: "breach-first-informational");

            return;
        }

        BenchState.Record(id: RatioBenchId, median: median, baselineMedian: entry.Median, band: band, status: "within-band");
    }

    private static bool Breach(double value, double baseline, double band) =>
        (Math.Abs(value: (value - baseline)) > band);
    private static List<double> MeasureRatios(int samples) {
        var seed = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.3));
        var rotation = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.017));
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.NegativeOne);
        var elementSeed = new QuadraticAlgebra<FixedQ4816>.Element(U: seed.Real, V: seed.Imaginary);
        var elementStep = new QuadraticAlgebra<FixedQ4816>.Element(U: rotation.Real, V: rotation.Imaginary);
        var ratios = new List<double>();

        for (var sample = 0; (sample < samples); ++sample) {
            var hand = Bench.BestNsPerOp(ops: Iterations, runs: 9, loop: () => BenchLoops.ComplexHand(iterations: Iterations, rotation: rotation, seed: seed));
            var generic = Bench.BestNsPerOp(ops: Iterations, runs: 9, loop: () => BenchLoops.ComplexGeneric(algebra: algebra, iterations: Iterations, seed: elementSeed, step: elementStep));

            ratios.Add(item: (generic / hand));
        }

        return ratios;
    }
}
