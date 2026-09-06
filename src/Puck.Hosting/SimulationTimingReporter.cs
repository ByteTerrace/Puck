using System.Diagnostics;

namespace Puck.Hosting;

/// <summary>
/// The world-simulation fixed-step's own worst-of-N CPU digest — the <c>[frame-timing] world-simulation …</c>
/// stderr line. Distinct from <see cref="FrameTimingHub"/> (the windowed launcher loop's own CPU buckets): this
/// reporter has no presentation dependency, so it runs in every boot shape, including headless. A caller
/// (<c>WorldHostStep</c>) reports one <see cref="Sample"/> per fixed step gated on <see cref="GpuTimingControl.Armed"/>
/// itself; this type only tracks the worst sample since the last emission and throttles the line to once every
/// <see cref="ReportInterval"/> reported samples.
/// </summary>
public sealed class SimulationTimingReporter(ulong reportInterval = 60UL) {
    private readonly ulong m_reportInterval = reportInterval;
    private ulong m_samples;
    private Sample m_worst;

    /// <summary>One fixed step's simulation CPU buckets, in <see cref="Stopwatch"/> ticks.</summary>
    /// <param name="Tick">The completed engine tick the sample was measured on.</param>
    /// <param name="PopulationTicks">Time spent stepping boot and every other world instance.</param>
    /// <param name="RosterTicks">Time spent preparing boot's seat intents for the step.</param>
    /// <param name="FinishTicks">Time spent finishing seat intents after every instance has stepped.</param>
    public readonly record struct Sample(ulong Tick, long PopulationTicks, long RosterTicks, long FinishTicks) {
        /// <summary>Gets the sum of every bucket — the ranking key for "worst".</summary>
        public long TotalTicks => ((PopulationTicks + RosterTicks) + FinishTicks);
    }

    /// <summary>Gets the reported-sample count the next emission is due at.</summary>
    public ulong ReportInterval => m_reportInterval;

    /// <summary>Folds one sample into the running worst-of-<see cref="ReportInterval"/> and, on the
    /// <see cref="ReportInterval"/>th call since the last emission, writes the digest line to stderr and resets.</summary>
    /// <param name="sample">This step's CPU buckets.</param>
    public void Report(in Sample sample) {
        m_samples++;

        if (sample.TotalTicks >= m_worst.TotalTicks) {
            m_worst = sample;
        }

        if (0UL != (m_samples % m_reportInterval)) {
            return;
        }

        var worst = m_worst;
        var frequency = Stopwatch.Frequency;

        static double ToMs(long ticks, long frequency) =>
            ((((double)ticks) * 1000.0) / frequency);

        Console.Error.WriteLine(value: $"[frame-timing] world-simulation worst-of-{m_reportInterval} tick {worst.Tick} total {ToMs(
            ticks: worst.TotalTicks,
            frequency: frequency
        ):0.000}ms | population {ToMs(
            ticks: worst.PopulationTicks,
            frequency: frequency
        ):0.000} | roster {ToMs(
            ticks: worst.RosterTicks,
            frequency: frequency
        ):0.000} | finish {ToMs(
            ticks: worst.FinishTicks,
            frequency: frequency
        ):0.000}");

        m_worst = default;
    }
}
