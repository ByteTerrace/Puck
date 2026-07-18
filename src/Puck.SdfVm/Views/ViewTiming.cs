using System.Diagnostics;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm.Views;

/// <summary>
/// [view-timing] plumbing shared by <see cref="SdfCameraView"/>, <see cref="NestedWorldView"/>, and
/// <see cref="ViewStack"/> — the same arming state <see cref="Puck.SdfVm.SdfEngineNode"/> reads for its GPU
/// <c>[world-timing]</c> digest, consulted through <see cref="GpuTimingControl.Shared"/> so three call sites do not
/// each duplicate the read (and so bench.run / the gpu.timing switch / the world.timing verb arm it live mid-session,
/// beyond the run-doc <c>host.timing</c> seed). Presentation-side only (Stopwatch ticks / GPU timestamp queries the host already
/// owns), never simulation state.
/// </summary>
internal static class ViewTiming {
    /// <summary>How many produced-frame samples between throttled digest lines (matches SdfEngineNode's cadence).</summary>
    internal const ulong ReportInterval = 60UL;

    /// <summary>Whether [view-timing] is armed right now (the live shared arming state — may flip mid-run).</summary>
    internal static bool Enabled => GpuTimingControl.Shared.Armed;

    /// <summary>A cheap timestamp mark — 0 (free) when <see cref="Enabled"/> is false.</summary>
    internal static long Mark() =>
        (Enabled ? Stopwatch.GetTimestamp() : 0L);

    /// <summary>Ticks elapsed since <paramref name="start"/> (a prior <see cref="Mark"/>), or 0 when disabled.</summary>
    internal static long Elapsed(long start) =>
        (Enabled ? (Stopwatch.GetTimestamp() - start) : 0L);

    /// <summary>Ticks converted to milliseconds via the Stopwatch frequency.</summary>
    internal static double Milliseconds(long ticks) =>
        (((double)ticks * 1000.0) / Stopwatch.Frequency);
}
