using System.Text.Json.Nodes;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>
/// What a host's simulation did between two readings: how many ticks it completed and how fast, what each tick cost,
/// how many owed steps it shed and where the frame thread's tick work went by named part. A window is opened and closed
/// on the host pump by the <see cref="HostFrameTimeline"/> that feeds it, so a remote reader (an authority's
/// <c>host.frames ticks</c> verb) takes the same part accounting a recording of the host's iterations holds, without
/// keeping a sample per iteration.
/// </summary>
internal sealed class HostTickWindow {
    private readonly long m_frequency;
    private readonly long m_startStamp;
    private readonly long m_shedAtStart;
    private readonly List<long> m_costs = new(capacity: 1024);
    private HostFrameWorkTimes m_parts;
    private long m_simulate;

    internal HostTickWindow(long frequency, long startStamp, long shedAtStart, in HostFrameWorkTimes openParts, long openSimulate) {
        m_frequency = frequency;
        m_startStamp = startStamp;
        m_shedAtStart = shedAtStart;
        for (var part = 0; part < (int)HostFrameWorkPart.Count; part++) {
            m_parts[part] = -openParts[part];
        }
        m_simulate = -openSimulate;
    }

    // Records one completed tick's cost, in the timeline's clock ticks.
    internal void AddTick(long cost) => m_costs.Add(cost);
    // Adds the named work and simulate phase of a closed iteration, or of the open one as the window ends.
    internal void AddIteration(in HostFrameWorkTimes parts, long simulate) {
        for (var part = 0; part < (int)HostFrameWorkPart.Count; part++) {
            m_parts[part] += parts[part];
        }
        m_simulate += simulate;
    }

    /// <summary>Describes the window as a JSON object: the ticks completed, the seconds they took and their rate, the
    /// owed steps shed, the tick's cost as a mean, median, 99th percentile and maximum (what a step thread spent
    /// executing it, or the pump thread's time in the step when it executes there), the tick parts' total milliseconds
    /// and their mean per tick, the pump's simulate phase and the cost's total.</summary>
    /// <param name="now">The timeline's clock now.</param>
    /// <param name="shed">The timeline's host-lifetime shed-step count now.</param>
    /// <returns>The object.</returns>
    internal JsonObject Describe(long now, long shed) {
        var count = m_costs.Count;
        var costs = new double[count];
        var total = 0.0;

        for (var index = 0; index < count; index++) {
            costs[index] = Milliseconds(m_costs[index]);
            total += costs[index];
        }
        Array.Sort(array: costs);

        var seconds = ((now - m_startStamp) / (double)m_frequency);
        var cost = HostFrameDistribution.Of(sorted: costs, total: total);

        return new JsonObject {
            ["ticks"] = count,
            ["seconds"] = seconds,
            ["ticksPerSecond"] = ((seconds > 0.0) ? (count / seconds) : 0.0),
            ["shed"] = (shed - m_shedAtStart),
            ["tickMs"] = new JsonObject { ["mean"] = cost.Mean, ["p50"] = cost.P50, ["p99"] = cost.P99, ["max"] = cost.Max },
            ["tickTotalMs"] = total,
            ["simulatePhaseMs"] = Milliseconds(m_simulate),
            ["partsMs"] = HostFrameRecording.DescribeParts(m_parts, HostFrameWorkPart.TickIngress, HostFrameWorkPart.Count),
            ["partsMeanMs"] = HostFrameRecording.DescribeParts(m_parts, HostFrameWorkPart.TickIngress, HostFrameWorkPart.Count, divisor: Math.Max(1, count)),
        };
    }

    private double Milliseconds(long ticks) => ((ticks * 1000.0) / m_frequency);
}
