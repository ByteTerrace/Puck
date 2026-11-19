using Puck.Maths;
using Puck.Hosting;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Puck.Launcher;

/// <summary>One host iteration as a <see cref="HostFrameRecording"/> keeps it. Durations are in the timeline's clock
/// ticks (<see cref="HostFrameRecording.Milliseconds"/> converts them).</summary>
public struct HostFrameSample {
    /// <summary>The iteration's start, in ticks since the recording's first iteration began.</summary>
    public long Start;
    /// <summary>The interval from the iteration's start to the next one's.</summary>
    public long Interval;
    /// <summary>Window events and input capture.</summary>
    public long Input;
    /// <summary>The console command drain.</summary>
    public long Commands;
    /// <summary>The fixed-step simulation's whole steps.</summary>
    public long Simulate;
    /// <summary>The presenter's wait for the previous frame's GPU work.</summary>
    public long Begin;
    /// <summary>The render root's frame.</summary>
    public long Produce;
    /// <summary>The capture controller and the present.</summary>
    public long Present;
    /// <summary>The pacer's wait for the next slot.</summary>
    public long Wait;
    /// <summary>The part of <see cref="Produce"/> the frame thread spent blocked on submission fences.</summary>
    public long ProduceBlocked;
    /// <summary>Exclusive work at the named render and tick boundaries, in stopwatch ticks.</summary>
    public HostFrameWorkTimes Parts;
    /// <summary>The number of scheduled render nodes.</summary>
    public int ScheduledRenders;
    internal int FirstWorld;
    internal int WorldCount;
    /// <summary>The garbage collector's pause time inside the iteration.</summary>
    public long Pause;
    /// <summary>How late the pacer woke.</summary>
    public long Late;
    /// <summary>The bytes every thread allocated inside the iteration.</summary>
    public long Allocated;
    /// <summary>The bytes the frame thread allocated inside the iteration.</summary>
    public long ThreadAllocated;
    /// <summary>The fixed steps the iteration ran.</summary>
    public int Steps;
    /// <summary>Generation 0 collections inside the iteration.</summary>
    public int Gen0;
    /// <summary>Generation 1 collections inside the iteration.</summary>
    public int Gen1;
    /// <summary>Generation 2 collections inside the iteration.</summary>
    public int Gen2;
    /// <summary>What <see cref="HostFrameRecording.Flags"/> answered as the iteration closed.</summary>
    public int Flags;
    /// <summary>The simulation tick last completed as the iteration closed, as <see cref="HostFrameRecording.Ticks"/>
    /// answered, or zero when the recording reads none.</summary>
    public ulong Tick;

    /// <summary>Gets the iteration's work: its interval less the pacer's wait.</summary>
    public readonly long Work => (Interval - Wait);
}
/// <summary>The statistics of a span of recorded iterations. Times are milliseconds. An iteration is the host loop's
/// own interval, never the time a frame stood on the display.</summary>
/// <param name="Frames">The iterations in the span.</param>
/// <param name="Seconds">The sum of their intervals.</param>
/// <param name="Interval">The interval's mean, median, 95th, 99th and 99.9th percentile and maximum.</param>
/// <param name="Work">The same of the work, the interval less the pacer's wait.</param>
/// <param name="Over">Iterations whose work passed the budget.</param>
/// <param name="OverTwice">Iterations whose work passed twice the budget.</param>
/// <param name="Over50">Iterations whose work passed 50 ms.</param>
/// <param name="Over100">Iterations whose work passed 100 ms.</param>
/// <param name="WorstIndex">The index of the iteration with the longest interval, or -1 for an empty span.</param>
/// <param name="PacingDeviation">The standard deviation of the interval.</param>
/// <param name="PacingStep">The mean absolute difference between successive intervals.</param>
/// <param name="Steps">The fixed steps the span ran.</param>
/// <param name="Collections">Collections by generation.</param>
/// <param name="PauseTotal">The collector's pause time in the span.</param>
/// <param name="PauseLongest">The longest pause inside one iteration.</param>
/// <param name="AllocatedPerFrame">The mean bytes every thread allocated per iteration.</param>
/// <param name="Flagged">Iterations whose flags were nonzero.</param>
/// <param name="LongestStall">The longest run of iterations that stepped no tick, in milliseconds.</param>
public sealed record HostFrameStatistics(
    int Frames, double Seconds, HostFrameDistribution Interval, HostFrameDistribution Work, int Over, int OverTwice, int Over50, int Over100, int WorstIndex,
    double PacingDeviation, double PacingStep, long Steps, (int Gen0, int Gen1, int Gen2) Collections, double PauseTotal, double PauseLongest, double AllocatedPerFrame,
    int Flagged, double LongestStall
) {
    /// <summary>Gets frames over seconds, or zero for an empty span.</summary>
    public double AverageRate => ((Seconds > 0.0) ? (Frames / Seconds) : 0.0);
    /// <summary>Gets the 99th percentile interval written as a rate: the 1% low in its percentile form. A span of
    /// fewer than 100 iterations has none.</summary>
    public double? Low1 => (((Frames >= 100) && (Interval.P99 > 0.0)) ? (1000.0 / Interval.P99) : null);
    /// <summary>Gets the 99.9th percentile interval written as a rate. A span of fewer than 1000 iterations has
    /// none.</summary>
    public double? Low01 => (((Frames >= 1000) && (Interval.P999 > 0.0)) ? (1000.0 / Interval.P999) : null);
}
/// <summary>A distribution's summary, in milliseconds.</summary>
/// <param name="Mean">The mean.</param>
/// <param name="P50">The median.</param>
/// <param name="P95">The 95th percentile.</param>
/// <param name="P99">The 99th percentile.</param>
/// <param name="P999">The 99.9th percentile.</param>
/// <param name="Max">The maximum.</param>
public readonly record struct HostFrameDistribution(double Mean, double P50, double P95, double P99, double P999, double Max) {
    internal static HostFrameDistribution Of(double[] sorted, double total) => ((sorted.Length == 0)
        ? default
        : new HostFrameDistribution(
            Max: sorted[^1],
            Mean: (total / sorted.Length),
            P50: OrderStatistics.Quantile(ascending: sorted, fraction: 0.5),
            P95: OrderStatistics.Quantile(ascending: sorted, fraction: 0.95),
            P99: OrderStatistics.Quantile(ascending: sorted, fraction: 0.99),
            P999: OrderStatistics.Quantile(ascending: sorted, fraction: 0.999)
        )
    );
}
/// <summary>
/// A bounded recording of every host iteration over a run: a second sink for the samples <see cref="HostFrameTimeline"/>
/// takes for its ring, kept past the ring's <see cref="HostFrameTimeline.Capacity"/>. Its storage is allocated when it
/// is created, so recording allocates nothing; an iteration past its capacity is counted in <see cref="Dropped"/>,
/// never stored and never hidden.
/// </summary>
/// <remarks>The timeline writes on the host pump; a reader on that thread sees every completed iteration.</remarks>
public sealed class HostFrameRecording {
    /// <summary>The most iterations one recording holds.</summary>
    public const int MaxCapacity = (1 << 20);

    private readonly HostFrameSample[] m_samples;
    private readonly List<HostWorldWork> m_worlds = [];
    private long m_origin;

    /// <summary>Creates a recording.</summary>
    /// <param name="capacity">The iterations to hold, at most <see cref="MaxCapacity"/>.</param>
    public HostFrameRecording(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: capacity, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: capacity, other: MaxCapacity);

        m_samples = new HostFrameSample[capacity];
    }

    /// <summary>Gets the iterations held.</summary>
    public int Count { get; private set; }
    /// <summary>Gets the iterations that closed after the recording was full.</summary>
    public long Dropped { get; private set; }
    /// <summary>Gets or sets what each iteration's <see cref="HostFrameSample.Flags"/> reads as it closes: the modes a
    /// reader of the recording must know held. Runs on the host pump every iteration, so it reads fields and nothing
    /// more.</summary>
    public Func<int>? Flags { get; set; }
    /// <summary>Gets or sets the simulation tick last completed, which each iteration's <see cref="HostFrameSample.Tick"/>
    /// reads as it closes. Runs on the host pump every iteration, so it reads a field and nothing more.</summary>
    public Func<ulong>? Ticks { get; set; }
    /// <summary>Gets the clock ticks in a second, set when a timeline starts writing.</summary>
    public long Frequency { get; internal set; } = 1L;
    /// <summary>Gets the held iterations, oldest first.</summary>
    public ReadOnlySpan<HostFrameSample> Samples => m_samples.AsSpan(start: 0, length: Count);

    internal void Add(HostFrameSample sample, long start, ReadOnlySpan<HostWorldWork> worlds = default) {
        if (Count == 0) {
            m_origin = start;
        }
        if (Count == m_samples.Length) {
            Dropped++;

            return;
        }

        sample.Start = (start - m_origin);
        sample.Flags = (Flags?.Invoke() ?? 0);
        sample.Tick = (Ticks?.Invoke() ?? 0UL);
        sample.FirstWorld = m_worlds.Count;
        sample.WorldCount = worlds.Length;
        m_worlds.AddRange(worlds);
        m_samples[Count++] = sample;
    }

    /// <summary>Gets the per-world timings of one recorded iteration.</summary>
    public ReadOnlySpan<HostWorldWork> Worlds(int iteration) {
        ref readonly var sample = ref Samples[iteration];
        return CollectionsMarshal.AsSpan(m_worlds).Slice(sample.FirstWorld, sample.WorldCount);
    }

    /// <summary>Converts clock ticks to milliseconds.</summary>
    /// <param name="ticks">The ticks.</param>
    /// <returns>The milliseconds.</returns>
    public double Milliseconds(long ticks) => ((ticks * 1000.0) / Frequency);
    /// <summary>Converts named work's stopwatch ticks to milliseconds.</summary>
    public static double WorkMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    /// <summary>Reads the statistics of a span of the recording.</summary>
    /// <param name="from">The first iteration.</param>
    /// <param name="to">The end of the span, exclusive.</param>
    /// <param name="budgetMilliseconds">The work past which an iteration is over.</param>
    /// <returns>The statistics; an empty span reads zeros.</returns>
    public HostFrameStatistics Statistics(int from, int to, double budgetMilliseconds) {
        from = Math.Clamp(value: from, min: 0, max: Count);
        to = Math.Clamp(value: to, min: from, max: Count);

        var count = (to - from);
        var intervals = new double[count];
        var works = new double[count];
        var intervalTotal = 0.0;
        var workTotal = 0.0;
        var squares = 0.0;
        var stepTotal = 0.0;
        var over = 0;
        var overTwice = 0;
        var over50 = 0;
        var over100 = 0;
        var worst = -1;
        var steps = 0L;
        var gen0 = 0;
        var gen1 = 0;
        var gen2 = 0;
        var pauseTotal = 0.0;
        var pauseLongest = 0.0;
        var allocated = 0L;
        var flagged = 0;
        var stall = 0.0;
        var longestStall = 0.0;

        for (var index = 0; (index < count); index++) {
            ref readonly var sample = ref m_samples[(from + index)];
            var interval = Milliseconds(ticks: sample.Interval);
            var work = Milliseconds(ticks: sample.Work);
            var pause = Milliseconds(ticks: sample.Pause);

            intervals[index] = interval;
            works[index] = work;
            intervalTotal += interval;
            workTotal += work;
            squares += (interval * interval);
            if (index > 0) { stepTotal += Math.Abs(value: (interval - intervals[(index - 1)])); }
            if ((worst < 0) || (interval > intervals[(worst - from)])) { worst = (from + index); }
            over += ((work > budgetMilliseconds) ? 1 : 0);
            overTwice += ((work > (2.0 * budgetMilliseconds)) ? 1 : 0);
            over50 += ((work > 50.0) ? 1 : 0);
            over100 += ((work > 100.0) ? 1 : 0);
            steps += sample.Steps;
            gen0 += sample.Gen0;
            gen1 += sample.Gen1;
            gen2 += sample.Gen2;
            pauseTotal += pause;
            pauseLongest = Math.Max(val1: pauseLongest, val2: pause);
            allocated += sample.Allocated;
            flagged += ((sample.Flags != 0) ? 1 : 0);
            stall = ((sample.Steps == 0) ? (stall + interval) : 0.0);
            longestStall = Math.Max(val1: longestStall, val2: stall);
        }

        var mean = ((count > 0) ? (intervalTotal / count) : 0.0);

        Array.Sort(array: intervals);
        Array.Sort(array: works);

        return new HostFrameStatistics(
            AllocatedPerFrame: ((count > 0) ? ((double)allocated / count) : 0.0),
            Collections: (gen0, gen1, gen2),
            Flagged: flagged,
            Frames: count,
            Interval: HostFrameDistribution.Of(sorted: intervals, total: intervalTotal),
            LongestStall: longestStall,
            Over: over,
            Over100: over100,
            Over50: over50,
            OverTwice: overTwice,
            PacingDeviation: ((count > 0) ? Math.Sqrt(d: Math.Max(val1: 0.0, val2: ((squares / count) - (mean * mean)))) : 0.0),
            PacingStep: ((count > 1) ? (stepTotal / (count - 1)) : 0.0),
            PauseLongest: pauseLongest,
            PauseTotal: pauseTotal,
            Seconds: (intervalTotal / 1000.0),
            Steps: steps,
            Work: HostFrameDistribution.Of(sorted: works, total: workTotal),
            WorstIndex: worst
        );
    }
}
