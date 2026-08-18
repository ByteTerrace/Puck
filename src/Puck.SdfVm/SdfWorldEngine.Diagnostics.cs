using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Looks up a named pass's milliseconds in a <see cref="TryReadPassTimings"/> result. Returns 0 when the
    /// label is absent (a pass renamed or removed), so a fixed-column consumer (the bench's beam/views/composite) keeps
    /// comparing across a pass-list change instead of hard-failing on a missing tuple element.</summary>
    /// <param name="passMilliseconds">A filled <see cref="TryReadPassTimings"/> result span.</param>
    /// <param name="passCount">The entry count that read reported.</param>
    /// <param name="label">One of <see cref="PassTimingLabels"/>.</param>
    /// <returns>The pass's milliseconds, or 0 when the label is not present.</returns>
    public static double PassMilliseconds(ReadOnlySpan<double> passMilliseconds, int passCount, string label) {
        for (var index = 0; ((index < passCount) && (index < PassLabels.Length)); index++) {
            if (string.Equals(
                a: PassLabels[index],
                b: label,
                comparisonType: StringComparison.Ordinal
            )) {
                return passMilliseconds[index];
            }
        }

        return 0.0;
    }
    /// <summary>Reads frame N−<see cref="FrameRingSize"/>'s per-pass GPU times — the newest frame the ring's slot
    /// fence proves retired, so a pipelining fire-and-forget host reads them complete with no added stall (the
    /// TimingPoolCount rotating pools). Fills <paramref name="passMilliseconds"/> with one entry per
    /// <see cref="PassTimingLabels"/> (same order) and reports the whole-frame span separately.</summary>
    /// <param name="passMilliseconds">Receives each pass's milliseconds, in <see cref="PassTimingLabels"/> order; must be
    /// at least <see cref="PassTimingCount"/> long.</param>
    /// <param name="passCount">The number of pass entries written (equals <see cref="PassTimingCount"/> on success, 0 otherwise).</param>
    /// <param name="frame">The whole-frame (frame-start → last-close) milliseconds.</param>
    /// <returns>Whether timing is live and the previous frame's marks were readable.</returns>
    public bool TryReadPassTimings(Span<double> passMilliseconds, out int passCount, out double frame) {
        passCount = 0;
        frame = 0.0;

        // The pools may not exist yet (live-armed mode before the first armed frame), and m_timingFrame counts only
        // timed frames, so the warmup guard holds across arm/disarm gaps — a disarmed frame simply leaves the last
        // timed frame's pool readable rather than advancing past it.
        if (
            (m_timingPools is null) ||
            (m_timingFrame < ((ulong)TimingPoolCount))
        ) {
            return false;
        }

        // After frame k's submit m_timingFrame is k+1; frame k−FrameRingSize recorded into pool
        // (k − FrameRingSize) % TimingPoolCount == (k + 1) % TimingPoolCount (the pool counts differ by one), and
        // that pool is not reset again until frame k+1 — so this read targets a complete, stable pool.
        var previousPool = m_timingPools![((int)(m_timingFrame % ((ulong)TimingPoolCount)))];
        Span<ulong> ticks = stackalloc ulong[((int)TimingMarkCount)];

        if (m_timingRecorder!.ReadTimestamps(
            deviceHandle: m_deviceHandle,
            firstQuery: 0,
            poolHandle: previousPool.PoolHandle,
            queryCount: TimingMarkCount,
            rawTicks: ticks
        ) < TimingMarkCount) {
            return false;
        }

        var count = PassLabels.Length;

        for (var index = 0; (index < count); index++) {
            passMilliseconds[index] = m_timingCapabilities.TicksToMilliseconds(
                startTicks: ticks[index],
                endTicks: ticks[(index + 1)]
            );
        }

        passCount = count;
        frame = m_timingCapabilities.TicksToMilliseconds(
            startTicks: ticks[0],
            endTicks: ticks[((int)(TimingMarkCount - 1U))]
        );

        return (frame > 0.0);
    }

    /// <summary>Gets the GPU time (in milliseconds) of the last <see cref="RenderFrame"/> when opt-in timing was
    /// enabled at construction — the frame-start → composite-close bracket of the four per-pass marks — or
    /// <see langword="null"/> when timing is disabled or the timestamps were not yet readable.</summary>
    public double? LastFrameGpuMilliseconds => m_lastFrameGpuMilliseconds;
    /// <summary>Gets the CPU wall-clock cost (milliseconds) of the most recently produced frame's per-frame instance-grid
    /// rebuild (<see cref="SdfProgram.BuildFrameInstanceGrid"/> + the ring slot's buffer write), or <see langword="null"/>
    /// when the live program's grid is invariant (built once at <see cref="UploadProgram"/>) and this frame skipped the
    /// rebuild. The CPU-bound counterpart to <see cref="LastFrameGpuMilliseconds"/> — a per-frame-moving dynamic
    /// instance set (e.g. <c>SdfBenchWorkload.DynamicMatrix</c>'s Moving rungs) forces this every frame; a static
    /// program never sets it. Plain wall-clock timing (not a GPU query) — this work runs entirely on the CPU.</summary>
    public double? LastInstanceGridRebuildMilliseconds => m_lastInstanceGridRebuildMilliseconds;
    /// <summary>The number of render passes a <see cref="TryReadPassTimings"/> read reports — the width a caller sizes
    /// its milliseconds span to (<see cref="PassTimingLabels"/> has the same length).</summary>
    public static int PassTimingCount => PassLabels.Length;
    /// <summary>The render passes' labels, in submission order — the names a <see cref="TryReadPassTimings"/> read fills
    /// alongside their milliseconds (pass <c>i</c> spans timing mark <c>i</c>..<c>i+1</c>). A fixed-column consumer (the
    /// bench) looks one up by name via <see cref="PassMilliseconds"/>; an iterating consumer (the <c>sdf.info</c> verb,
    /// the <c>[world-timing]</c> line) walks them in order, so a future pass surfaces everywhere with no consumer edit.</summary>
    public static ReadOnlySpan<string> PassTimingLabels => PassLabels;
    /// <summary>Gets the GPU timestamp capabilities when opt-in timing was enabled (period/valid-bits for digests).</summary>
    public GpuTimestampCapabilities TimingCapabilities => m_timingCapabilities;
    /// <summary>Gets whether opt-in GPU timing is available (a supported factory + recorder were supplied). In eager
    /// mode every frame is timed; in live-armed mode a frame is timed only while <see cref="GpuTimingControl.Shared"/>
    /// is armed — see <see cref="SdfWorldEngineOptions.LiveArmedTiming"/>.</summary>
    public bool TimingEnabled => m_timingAvailable;
}
