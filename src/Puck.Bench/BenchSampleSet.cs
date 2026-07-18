namespace Puck.Bench;

/// <summary>
/// The binned statistics for one channel of millisecond samples — the everything-as-data shape a report row and the
/// stdout table both read (<c>{ meanMs, medianMs, p95Ms, p99Ms, minMs }</c> plus the max the verdict math wants).
/// </summary>
/// <param name="Count">The number of samples the stats summarize.</param>
/// <param name="MeanMs">The arithmetic mean, in milliseconds.</param>
/// <param name="MedianMs">The 50th-percentile value, in milliseconds.</param>
/// <param name="P95Ms">The 95th-percentile value, in milliseconds.</param>
/// <param name="P99Ms">The 99th-percentile value, in milliseconds.</param>
/// <param name="MinMs">The smallest value, in milliseconds.</param>
/// <param name="MaxMs">The largest value, in milliseconds.</param>
public readonly record struct BenchChannelStats(
    int Count,
    double MeanMs,
    double MedianMs,
    double P95Ms,
    double P99Ms,
    double MinMs,
    double MaxMs
);

/// <summary>
/// The WALL-interval channel's scoring statistics — the binned distribution plus the throughput-and-variance figures
/// the score model and the variance policy consume. Distinct from a plain <see cref="BenchChannelStats"/> because the
/// wall channel carries the spike policy (§6): a spike is EXCLUDED from the throughput mean but INCLUDED in the tail
/// (p99, 1%-low), so a score is not a lottery yet the tail still tells the truth about stutter.
/// </summary>
/// <param name="Binned">The binned distribution over EVERY sampled frame (spikes included).</param>
/// <param name="SpikeFrames">The count of frames whose interval exceeded the spike threshold.</param>
/// <param name="ThroughputFps">True delivered throughput — <c>1000 × keptFrames / Σ(keptIntervalMs)</c> over the
/// non-spike frames (the arithmetic mean of frame times, inverted).</param>
/// <param name="OnePercentLowFps">The mean of the slowest one percent of frame times (spikes included), inverted —
/// the standard "1% low" stutter figure.</param>
/// <param name="Noisy">Whether the run was noisy — <c>p95 / median &gt; 1.5</c>.</param>
public readonly record struct BenchWallStats(
    BenchChannelStats Binned,
    int SpikeFrames,
    double ThroughputFps,
    double OnePercentLowFps,
    bool Noisy
);

/// <summary>
/// A fixed-capacity accumulator for one channel of millisecond samples, filled once per frame from the render-thread
/// timing publish. <see cref="Add"/> is allocation-free (a pre-sized backing array written by index), so the per-frame
/// hot path only writes doubles; the sorting/percentile work happens once per scene at finalize, off the frame path.
/// </summary>
public sealed class BenchSampleSet {
    private readonly double[] m_samples;
    private int m_count;

    /// <summary>Creates a sample set sized to hold <paramref name="capacity"/> samples (at least one).</summary>
    /// <param name="capacity">The expected sample count (the scene's sample-frame count, or a channel's expected
    /// depth); samples beyond it are dropped rather than growing the buffer on the hot path.</param>
    public BenchSampleSet(int capacity) {
        m_samples = new double[Math.Max(val1: 1, val2: capacity)];
    }

    /// <summary>The number of samples recorded so far.</summary>
    public int Count => m_count;

    /// <summary>The recorded samples, in insertion order.</summary>
    public ReadOnlySpan<double> Samples => m_samples.AsSpan(start: 0, length: m_count);

    /// <summary>Records one sample, in milliseconds. A no-op once the capacity is reached — the harness sizes the
    /// buffer to the scene's sample count, so this only guards against an over-run, never silently truncates a
    /// correctly-sized run.</summary>
    /// <param name="milliseconds">The sample value.</param>
    public void Add(double milliseconds) {
        if (m_count < m_samples.Length) {
            m_samples[m_count++] = milliseconds;
        }
    }

    /// <summary>Copies the recorded samples out (for the raw-sample escape hatch a <c>samples</c> run dumps).</summary>
    /// <returns>A fresh array of the recorded samples.</returns>
    public double[] ToArray() => Samples.ToArray();

    /// <summary>Computes the binned distribution over every recorded sample. Sorts a scratch copy — call once, at
    /// finalize.</summary>
    /// <returns>The channel's binned statistics (an empty distribution when no samples landed).</returns>
    public BenchChannelStats Stats() {
        if (m_count == 0) {
            return default;
        }

        var sorted = SortedCopy();
        var sum = 0.0;

        for (var index = 0; (index < m_count); index++) {
            sum += m_samples[index];
        }

        return new BenchChannelStats(
            Count: m_count,
            MeanMs: (sum / m_count),
            MedianMs: Percentile(sorted: sorted, fraction: 0.50),
            MinMs: sorted[0],
            MaxMs: sorted[(m_count - 1)],
            P95Ms: Percentile(sorted: sorted, fraction: 0.95),
            P99Ms: Percentile(sorted: sorted, fraction: 0.99)
        );
    }

    /// <summary>The 50th-percentile value over EVERY recorded sample (the DVFS canary reports this as the beam pass's
    /// whole-scene <c>beamMedianMs</c>). Zero when empty.</summary>
    /// <returns>The median, in milliseconds.</returns>
    public double Median() {
        if (m_count == 0) {
            return 0.0;
        }

        return Percentile(sorted: SortedCopy(), fraction: 0.50);
    }

    /// <summary>The median over a contiguous slice of the recorded samples taken IN INSERTION (time) ORDER — the
    /// half-open fraction range <c>[startFraction, endFraction)</c> of the run's timeline. The WITHIN-SCENE DVFS
    /// canary (§6) reads the first third (<c>0 → 1/3</c>) and the last third (<c>2/3 → 1</c>) of the beam pass and
    /// compares their medians: a scene running a CONSTANT workload whose late frames drift from its early frames is
    /// the honest clock-sag signal. Insertion order matters here (unlike <see cref="Median"/>, which sorts the whole
    /// window), so the slice is sorted locally before the percentile is taken. Zero when the slice is empty.</summary>
    /// <param name="startFraction">The slice's inclusive start as a fraction of the recorded count, in <c>[0,1]</c>.</param>
    /// <param name="endFraction">The slice's exclusive end as a fraction of the recorded count, in <c>[0,1]</c>.</param>
    /// <returns>The slice's median, in milliseconds.</returns>
    public double MedianOfTimeSlice(double startFraction, double endFraction) {
        if (m_count == 0) {
            return 0.0;
        }

        var start = Math.Clamp(value: (int)(startFraction * m_count), min: 0, max: m_count);
        var end = Math.Clamp(value: (int)Math.Ceiling(a: (endFraction * m_count)), min: start, max: m_count);

        if (end <= start) {
            return 0.0;
        }

        var slice = m_samples.AsSpan(start: start, length: (end - start)).ToArray();

        Array.Sort(array: slice);

        return Percentile(sorted: slice, fraction: 0.50);
    }

    /// <summary>Computes the wall channel's throughput-and-variance statistics under the spike policy.
    /// A frame whose interval exceeds <paramref name="spikeFactor"/> × the median is a spike: excluded from the
    /// throughput mean, still counted in the binned distribution and the 1%-low tail.</summary>
    /// <param name="spikeFactor">The spike multiplier over the median (the policy value is 4).</param>
    /// <returns>The wall channel's scoring statistics.</returns>
    public BenchWallStats WallStats(double spikeFactor) {
        var binned = Stats();

        if (m_count == 0) {
            return new BenchWallStats(Binned: binned, Noisy: false, OnePercentLowFps: 0.0, SpikeFrames: 0, ThroughputFps: 0.0);
        }

        var threshold = (binned.MedianMs * spikeFactor);
        var keptSum = 0.0;
        var keptCount = 0;
        var spikes = 0;

        for (var index = 0; (index < m_count); index++) {
            var value = m_samples[index];

            if ((value > threshold) && (threshold > 0.0)) {
                spikes++;
            } else {
                keptSum += value;
                keptCount++;
            }
        }

        var throughputFps = (((keptCount > 0) && (keptSum > 0.0)) ? ((1000.0 * keptCount) / keptSum) : 0.0);

        // The 1%-low: the mean of the slowest 1% of frame times (spikes INCLUDED), inverted. Nearest-rank count,
        // at least one frame.
        var sorted = SortedCopy();
        var worstCount = Math.Max(val1: 1, val2: ((m_count + 99) / 100));
        var worstSum = 0.0;

        for (var index = 0; (index < worstCount); index++) {
            worstSum += sorted[((m_count - 1) - index)];
        }

        var onePercentLowFps = ((worstSum > 0.0) ? ((1000.0 * worstCount) / worstSum) : 0.0);
        var noisy = ((binned.MedianMs > 0.0) && ((binned.P95Ms / binned.MedianMs) > 1.5));

        return new BenchWallStats(
            Binned: binned,
            Noisy: noisy,
            OnePercentLowFps: onePercentLowFps,
            SpikeFrames: spikes,
            ThroughputFps: throughputFps
        );
    }

    // A sorted-ascending copy of the recorded prefix — one allocation per finalize call, never on the frame path.
    private double[] SortedCopy() {
        var sorted = Samples.ToArray();

        Array.Sort(array: sorted);

        return sorted;
    }

    // Nearest-rank percentile over a sorted-ascending array (fraction in [0,1]).
    private double Percentile(double[] sorted, double fraction) {
        if (sorted.Length == 0) {
            return 0.0;
        }

        var rank = (int)Math.Ceiling(a: (fraction * sorted.Length));
        var index = Math.Clamp(value: (rank - 1), min: 0, max: (sorted.Length - 1));

        return sorted[index];
    }
}
