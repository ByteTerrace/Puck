using System.Diagnostics;
using Puck.Abstractions.Gpu;

namespace Puck.Demo.Overworld;

/// <summary>
/// [frame-timing] sub-bucket plumbing for <c>OverworldRenderNode.ProduceFrame</c> — hoisted out of the node so
/// the Stopwatch machinery adds exactly ONE coupled type there (the node is at its analyzer class-coupling ceiling;
/// see its own remarks). Presentation-side only (Stopwatch ticks, never simulation state), armed off the same
/// <see cref="GpuTimingControl.Shared"/> state <see cref="Puck.SdfVm.SdfEngineNode"/> reads for its GPU
/// <c>[world-timing]</c> digest (so bench.run / the gpu.timing switch / the world.timing verb turn it on live, beyond
/// the run-doc <c>host.timing</c> seed), throttled to once per <see cref="ReportInterval"/> PRODUCED frames so it isn't noisy.
/// </summary>
internal static class OverworldFrameTiming {
    private const ulong ReportInterval = 60UL; // once per second at 60 fps, matching [world-timing]'s cadence

    /// <summary>Whether the digest is armed right now (the live shared arming state — may flip mid-run).</summary>
    internal static bool Enabled => GpuTimingControl.Shared.Armed;

    /// <summary>A cheap timestamp mark — 0 (free) when <see cref="Enabled"/> is false, so a caller need not branch on
    /// the toggle itself at each call site.</summary>
    internal static long Mark() =>
        (Enabled ? Stopwatch.GetTimestamp() : 0L);

    /// <summary>Ticks elapsed since <paramref name="start"/> (a prior <see cref="Mark"/>), or 0 when disabled.</summary>
    internal static long Elapsed(long start) =>
        (Enabled ? (Stopwatch.GetTimestamp() - start) : 0L);

    /// <summary>Prints the throttled digest — a no-op unless armed and this is a report frame.</summary>
    /// <param name="producedFrames">The node's own produced-frame counter (post-increment), the same counter every
    /// other per-frame throttle in the node gates on.</param>
    /// <param name="tickFeedsTicks">Elapsed ticks for <c>OverworldFrameSource.TickFeeds</c>.</param>
    /// <param name="advanceSimulationTicks">Elapsed ticks for <c>OverworldRenderNode.AdvanceSimulation</c>.</param>
    /// <param name="produceMachinesTicks">Elapsed ticks for <c>OverworldRenderNode.ProduceMachines</c>.</param>
    /// <param name="bootedCount">How many of the room's cabinets are booted this frame.</param>
    /// <param name="cabinetCount">The room's total cabinet count.</param>
    internal static void Report(ulong producedFrames, long tickFeedsTicks, long advanceSimulationTicks, long produceMachinesTicks, int bootedCount, int cabinetCount) {
        if (
            !Enabled ||
            (producedFrames == 0UL) ||
            (0UL != (producedFrames % ReportInterval))
        ) {
            return;
        }

        static double Milliseconds(long ticks) =>
            (((double)ticks * 1000.0) / Stopwatch.Frequency);

        Console.Error.WriteLine(value: $"[frame-timing] overworld tickFeeds {Milliseconds(ticks: tickFeedsTicks):0.000}ms | advanceSimulation {Milliseconds(ticks: advanceSimulationTicks):0.000} | produceMachines {Milliseconds(ticks: produceMachinesTicks):0.000} (booted {bootedCount}/{cabinetCount})");
    }
}
