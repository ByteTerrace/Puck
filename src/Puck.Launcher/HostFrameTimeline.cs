using System.Globalization;
using System.Text;

namespace Puck.Launcher;

/// <summary>The phases of one windowed host iteration, in the order the loop runs them.</summary>
public enum HostFramePhase {
    /// <summary>Window events and input capture.</summary>
    Input,
    /// <summary>The console command drain and its echo flush.</summary>
    Commands,
    /// <summary>The fixed-step simulation's whole steps.</summary>
    Simulate,
    /// <summary>The presenter's frame start: its wait for the previous frame's GPU work.</summary>
    Begin,
    /// <summary>The render root's frame.</summary>
    Produce,
    /// <summary>The capture controller and the present.</summary>
    Present,
    /// <summary>The pacer's wait for the next slot.</summary>
    Wait,
}

/// <summary>
/// The windowed host's per-iteration timeline: a ring of the latest iterations, each the interval from its start to
/// the next one's, split into the loop's phases, with its fixed steps and the garbage collections and pause time that
/// fell inside it. The <c>host.frames</c> verb reads it. Every write and read runs on the host pump thread.
/// </summary>
/// <remarks>Recording allocates nothing; only a reading formats text.</remarks>
public sealed class HostFrameTimeline {
    /// <summary>The iterations the ring holds.</summary>
    public const int Capacity = 4096;

    private const int PhaseCount = 7;

    private readonly long[] m_phases = new long[(Capacity * PhaseCount)];
    private readonly long[] m_intervals = new long[Capacity];
    private readonly long[] m_pauses = new long[Capacity];
    private readonly int[] m_steps = new int[Capacity];
    private readonly long[] m_late = new long[Capacity];
    private readonly int[] m_collections = new int[(Capacity * 3)];
    private readonly long[] m_open = new long[PhaseCount];
    private readonly long m_frequency;

    private long m_count;
    private long m_iterationStart;
    private long m_phaseStart;
    private long m_pauseStart;
    private int m_gen0;
    private int m_gen1;
    private int m_gen2;
    private int m_openSteps;
    private long m_openLate;

    /// <summary>Creates a timeline over the given clock.</summary>
    /// <param name="time">The host loop's clock.</param>
    public HostFrameTimeline(TimeProvider time) {
        ArgumentNullException.ThrowIfNull(time);

        Time = time;
        m_frequency = time.TimestampFrequency;
    }

    /// <summary>Gets the clock the timeline reads.</summary>
    public TimeProvider Time { get; }

    /// <summary>Closes the iteration in progress, if any, and opens the next one.</summary>
    public void BeginIteration() {
        var now = Time.GetTimestamp();
        var pause = GC.GetTotalPauseDuration().Ticks;
        var gen0 = GC.CollectionCount(generation: 0);
        var gen1 = GC.CollectionCount(generation: 1);
        var gen2 = GC.CollectionCount(generation: 2);

        if (m_iterationStart != 0L) {
            var slot = ((int)(m_count % Capacity));

            m_intervals[slot] = (now - m_iterationStart);
            m_pauses[slot] = (pause - m_pauseStart);
            m_steps[slot] = m_openSteps;
            m_late[slot] = m_openLate;
            m_collections[(slot * 3)] = (gen0 - m_gen0);
            m_collections[((slot * 3) + 1)] = (gen1 - m_gen1);
            m_collections[((slot * 3) + 2)] = (gen2 - m_gen2);
            Array.Copy(
                destinationArray: m_phases,
                destinationIndex: (slot * PhaseCount),
                length: PhaseCount,
                sourceArray: m_open,
                sourceIndex: 0
            );
            m_count++;
        }

        Array.Clear(array: m_open);
        m_openSteps = 0;
        m_openLate = 0L;
        m_iterationStart = now;
        m_phaseStart = now;
        m_pauseStart = pause;
        m_gen0 = gen0;
        m_gen1 = gen1;
        m_gen2 = gen2;
    }
    /// <summary>Charges the time since the previous mark to <paramref name="phase"/>.</summary>
    /// <param name="phase">The phase that just ran.</param>
    public void Mark(HostFramePhase phase) {
        var now = Time.GetTimestamp();

        m_open[((int)phase)] += (now - m_phaseStart);
        m_phaseStart = now;
    }
    /// <summary>Records the fixed steps the iteration ran.</summary>
    /// <param name="steps">The step count.</param>
    public void NoteSteps(int steps) => m_openSteps += steps;
    /// <summary>Records how far past its deadline the pacer's wait returned, for an iteration whose wait slept.</summary>
    /// <param name="ticks">The overshoot, in the clock's timestamp ticks.</param>
    public void NoteLate(long ticks) => m_openLate = ticks;
    /// <summary>Gets the iterations recorded since the last <see cref="Reset"/>: a coordinate <see cref="Scan"/> reads
    /// a span of.</summary>
    public long Recorded => m_count;
    /// <summary>Reads the iterations from one coordinate to another: how many did more work than a budget, and the
    /// slowest. Work is an iteration's interval less the pacer's wait, so a late wake of the pacer is not a stall.</summary>
    /// <param name="from">The first iteration, a value of <see cref="Recorded"/> taken earlier; one the ring has since
    /// overwritten reads from the oldest it holds.</param>
    /// <param name="to">The end of the span, exclusive: a value of <see cref="Recorded"/>.</param>
    /// <param name="budgetMilliseconds">The work, in milliseconds, past which an iteration is over.</param>
    /// <returns>The iterations read, how many were over, and the most work and the longest interval among them, in
    /// milliseconds.</returns>
    public (int Iterations, int Over, double WorstWork, double WorstInterval) Scan(long from, long to, double budgetMilliseconds) {
        var first = Math.Max(
            val1: from,
            val2: Math.Max(
                val1: (m_count - Capacity),
                val2: 0L
            )
        );
        var last = Math.Min(
            val1: to,
            val2: m_count
        );
        var iterations = 0;
        var over = 0;
        var worstWork = 0.0;
        var worstInterval = 0.0;

        for (var index = first; (index < last); index++) {
            var slot = ((int)(index % Capacity));
            var interval = Milliseconds(ticks: m_intervals[slot]);
            var work = (interval - Milliseconds(ticks: m_phases[((slot * PhaseCount) + ((int)HostFramePhase.Wait))]));

            iterations++;
            over += ((work > budgetMilliseconds) ? 1 : 0);
            worstWork = Math.Max(
                val1: worstWork,
                val2: work
            );
            worstInterval = Math.Max(
                val1: worstInterval,
                val2: interval
            );
        }

        return (iterations, over, worstWork, worstInterval);
    }
    /// <summary>Forgets every recorded iteration.</summary>
    public void Reset() {
        m_count = 0L;
        m_iterationStart = 0L;
    }
    /// <summary>Describes the latest iterations: the interval's distribution, the count over a budget, the most work an
    /// iteration did, the pacer's greatest lateness, the collections, and the slowest iterations with their phases,
    /// followed by the busiest one when they do not hold it.</summary>
    /// <param name="frames">The iterations to read, at most <see cref="Capacity"/>.</param>
    /// <param name="budgetMilliseconds">The interval past which an iteration counts as over.</param>
    /// <param name="worst">The slowest iterations to list.</param>
    /// <returns>The description, one line per listed iteration after the summary.</returns>
    public string Describe(int frames, double budgetMilliseconds, int worst) {
        var available = ((int)Math.Min(
            val1: m_count,
            val2: Capacity
        ));
        var count = Math.Clamp(
            max: available,
            min: 0,
            value: frames
        );

        if (count == 0) {
            return "[host.frames: no iterations recorded]";
        }

        var indices = new long[count];
        var intervals = new double[count];

        for (var i = 0; (i < count); i++) {
            var index = ((m_count - count) + i);

            indices[i] = index;
            intervals[i] = Milliseconds(ticks: m_intervals[((int)(index % Capacity))]);
        }

        var sorted = intervals.ToArray();

        Array.Sort(array: sorted);

        var over = 0;
        var total = 0.0;
        var busiest = 0.0;
        var busiestAt = 0;
        var latest = 0.0;
        var collections = 0;
        var pause = 0L;

        for (var i = 0; (i < count); i++) {
            var slot = ((int)(indices[i] % Capacity));
            var work = (intervals[i] - Milliseconds(ticks: m_phases[((slot * PhaseCount) + ((int)HostFramePhase.Wait))]));

            total += intervals[i];
            collections += ((m_collections[(slot * 3)] + m_collections[((slot * 3) + 1)]) + m_collections[((slot * 3) + 2)]);
            pause += m_pauses[slot];
            if (work > busiest) {
                busiest = work;
                busiestAt = i;
            }
            latest = Math.Max(
                val1: latest,
                val2: Milliseconds(ticks: m_late[slot])
            );

            if (intervals[i] > budgetMilliseconds) {
                over++;
            }
        }

        var builder = new StringBuilder();

        builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"[host.frames: {count} iterations | avg={(total / count):0.00} p50={Percentile(sorted: sorted, fraction: 0.5):0.00} p99={Percentile(sorted: sorted, fraction: 0.99):0.00} max={sorted[^1]:0.00} ms | over {budgetMilliseconds:0.0} ms: {over} | busiest {busiest:0.00} ms of work | pacer late at most {latest:0.00} ms | {collections} collections pausing {TimeSpan.FromTicks(value: pause).TotalMilliseconds:0.00} ms]"
        );

        var order = Enumerable.Range(
            count: count,
            start: 0
        ).OrderByDescending(keySelector: i => intervals[i]).Take(count: worst).ToList();

        // The busiest iteration follows the slowest ones when they do not hold it, since a pacer's wait can hide its work.
        if ((worst > 0) && !order.Contains(item: busiestAt)) {
            order.Add(item: busiestAt);
        }

        foreach (var i in order) {
            var slot = ((int)(indices[i] % Capacity));
            var phases = (slot * PhaseCount);

            builder.Append(value: '\n');
            builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"  #{indices[i]} {intervals[i]:0.00} ms{((i == busiestAt) ? " (busiest)" : "")}: input={Milliseconds(ticks: m_phases[phases]):0.00} commands={Milliseconds(ticks: m_phases[(phases + 1)]):0.00} simulate={Milliseconds(ticks: m_phases[(phases + 2)]):0.00} ({m_steps[slot]} steps) begin={Milliseconds(ticks: m_phases[(phases + 3)]):0.00} produce={Milliseconds(ticks: m_phases[(phases + 4)]):0.00} present={Milliseconds(ticks: m_phases[(phases + 5)]):0.00} wait={Milliseconds(ticks: m_phases[(phases + 6)]):0.00} (late {Milliseconds(ticks: m_late[slot]):0.00}) gc={m_collections[(slot * 3)]}/{m_collections[((slot * 3) + 1)]}/{m_collections[((slot * 3) + 2)]} pause={TimeSpan.FromTicks(value: m_pauses[slot]).TotalMilliseconds:0.00}"
            );
        }

        return builder.ToString();
    }

    private double Milliseconds(long ticks) => ((ticks * 1000.0) / m_frequency);
    private static double Percentile(double[] sorted, double fraction) => sorted[Math.Min(
        val1: (sorted.Length - 1),
        val2: ((int)Math.Floor(d: (fraction * sorted.Length)))
    )];
}
