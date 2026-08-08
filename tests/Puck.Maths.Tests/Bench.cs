using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Puck.Maths.Tests;

/// <summary>
/// Module 5 — the bench harness. House best-of-N latency measurement over a monotonic tick clock, a machine
/// fingerprint, and a fixed spin calibration used to detect a busy machine. The seeded bench prefers a RATIO metric
/// (generic multiply versus the hand-written kernel) over absolute nanoseconds, so it is insensitive to clock scaling.
/// </summary>
internal static class Bench {
    /// <summary>Nanoseconds per <see cref="Stopwatch"/> tick.</summary>
    public static readonly double NsPerTick = (1_000_000_000.0 / Stopwatch.Frequency);

    /// <summary>A sink that keeps measured loops from being optimized away.</summary>
    public static long Sink;

    /// <summary>The machine fingerprint keying the baselines: CPU identifier plus logical core count.</summary>
    /// <returns>The fingerprint string.</returns>
    public static string Fingerprint() {
        var processor = (Environment.GetEnvironmentVariable(variable: "PROCESSOR_IDENTIFIER") ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());

        return $"{processor} x{Environment.ProcessorCount}";
    }

    /// <summary>Measures a fixed spin-calibration loop, best-of-five, in nanoseconds — the busy-machine proxy.</summary>
    /// <returns>The best observed wall time of the calibration work.</returns>
    public static double Calibrate() {
        const long Iterations = 20_000_000L;
        var best = double.MaxValue;

        for (var run = 0; (run < 5); ++run) {
            var start = Stopwatch.GetTimestamp();
            var accumulator = 0L;

            for (var i = 0L; (i < Iterations); ++i) {
                accumulator = unchecked((accumulator * 6364136223846793005L) + 1L);
            }

            Sink ^= accumulator;

            best = Math.Min(val1: best, val2: ((Stopwatch.GetTimestamp() - start) * NsPerTick));
        }

        return best;
    }

    /// <summary>Best-of-N nanoseconds per operation for a loop returning a guard value.</summary>
    /// <param name="ops">The operation count the loop performs.</param>
    /// <param name="runs">The number of measured runs; the best is taken.</param>
    /// <param name="loop">The loop, returning a guard XOR to defeat dead-code elimination.</param>
    /// <returns>The best nanoseconds per operation.</returns>
    public static double BestNsPerOp(long ops, int runs, Func<long> loop) {
        var guard = 0L;

        guard ^= loop();
        guard ^= loop();

        var best = double.MaxValue;

        for (var run = 0; (run < runs); ++run) {
            var start = Stopwatch.GetTimestamp();

            guard ^= loop();

            best = Math.Min(val1: best, val2: ((Stopwatch.GetTimestamp() - start) * NsPerTick));
        }

        Sink ^= guard;

        return (best / ops);
    }

    /// <summary>The median of a sample.</summary>
    /// <param name="values">The sample.</param>
    /// <returns>The median.</returns>
    public static double Median(IReadOnlyList<double> values) {
        var sorted = values.OrderBy(keySelector: static value => value).ToArray();
        var count = sorted.Length;

        return (((count & 1) == 1)
            ? sorted[count / 2]
            : (0.5 * (sorted[(count / 2) - 1] + sorted[count / 2])));
    }

    /// <summary>The median absolute deviation of a sample.</summary>
    /// <param name="values">The sample.</param>
    /// <returns>The MAD.</returns>
    public static double MedianAbsoluteDeviation(IReadOnlyList<double> values) {
        var median = Median(values: values);

        return Median(values: [.. values.Select(selector: value => Math.Abs(value: (value - median)))]);
    }
}

/// <summary>One machine's recorded baseline for a bench: the median ratio, the recorded per-run ratios, and their MAD.</summary>
internal sealed class BenchEntry {
    /// <summary>Gets or sets the bench id.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets the recorded median ratio.</summary>
    public double Median { get; set; }
    /// <summary>Gets or sets the recorded per-run ratios the median and MAD derive from.</summary>
    public List<double> Runs { get; set; } = [];
    /// <summary>Gets or sets the median absolute deviation of <see cref="Runs"/>.</summary>
    public double Mad { get; set; }
}

/// <summary>The recorded baseline for one machine fingerprint.</summary>
internal sealed class MachineBaseline {
    /// <summary>Gets or sets the machine fingerprint.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>Gets or sets the fixed spin-calibration nanoseconds; a live run more than 2× off is environment-suspect.</summary>
    public double CalibrationNs { get; set; }
    /// <summary>Gets or sets the per-bench baselines.</summary>
    public List<BenchEntry> Benches { get; set; } = [];
}

/// <summary>The committed per-machine bench baselines.</summary>
internal sealed class BaselineModel {
    /// <summary>Gets the machine baselines, ordered by fingerprint.</summary>
    public List<MachineBaseline> Machines { get; init; } = [];
}

/// <summary>Collects bench observations for the RESULTS ledger. Populated only when the bench tier runs.</summary>
internal static class BenchState {
    private static readonly object Gate = new();
    private static readonly List<Observation> ObservationList = [];

    /// <summary>Gets whether the bench tier ran this session.</summary>
    public static bool Ran { get; private set; }

    /// <summary>Records one bench observation.</summary>
    /// <param name="id">The bench id.</param>
    /// <param name="median">The measured median ratio.</param>
    /// <param name="baselineMedian">The baseline median ratio.</param>
    /// <param name="band">The tolerated noise band.</param>
    /// <param name="status">The outcome status.</param>
    public static void Record(string id, double median, double baselineMedian, double band, string status) {
        lock (Gate) {
            Ran = true;

            ObservationList.Add(item: new Observation(Id: id, Median: median, BaselineMedian: baselineMedian, Band: band, Status: status));
        }
    }

    /// <summary>Gets the recorded observations.</summary>
    /// <returns>A snapshot of the observations.</returns>
    public static IReadOnlyList<Observation> Observations() {
        lock (Gate) {
            return [.. ObservationList];
        }
    }

    /// <summary>One bench observation.</summary>
    /// <param name="Id">The bench id.</param>
    /// <param name="Median">The measured median ratio.</param>
    /// <param name="BaselineMedian">The baseline median ratio.</param>
    /// <param name="Band">The tolerated noise band.</param>
    /// <param name="Status">The outcome status.</param>
    public sealed record Observation(string Id, double Median, double BaselineMedian, double Band, string Status);
}

/// <summary>The measured latency loops for the seeded bench, kept out of line so the JIT cannot fold the by-parameter
/// algebra descriptor to a constant.</summary>
internal static class BenchLoops {
    /// <summary>The hand-written <see cref="FixedComplex"/> unit-rotation chain.</summary>
    /// <param name="seed">The chain seed.</param>
    /// <param name="rotation">The per-step rotation.</param>
    /// <param name="iterations">The iteration count.</param>
    /// <returns>A guard value.</returns>
    public static long ComplexHand(FixedComplex seed, FixedComplex rotation, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = (accumulator * rotation);
            sink ^= accumulator.Real.Value;
        }

        return sink;
    }

    /// <summary>The generic <see cref="QuadraticAlgebra{TScalar}"/> multiply chain for the same rotation.</summary>
    /// <param name="algebra">The by-parameter algebra descriptor.</param>
    /// <param name="seed">The chain seed.</param>
    /// <param name="step">The per-step element.</param>
    /// <param name="iterations">The iteration count.</param>
    /// <returns>A guard value.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    public static long ComplexGeneric(QuadraticAlgebra<FixedQ4816> algebra, QuadraticAlgebra<FixedQ4816>.Element seed, QuadraticAlgebra<FixedQ4816>.Element step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: step);
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
}
