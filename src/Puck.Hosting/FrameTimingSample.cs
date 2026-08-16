namespace Puck.Hosting;

/// <summary>
/// One produced frame's CPU-side wall-clock buckets, published by the launcher's window loop into
/// <see cref="FrameTimingHub"/>. The twelve named phase buckets plus <see cref="RemainderMs"/> tile the loop-top-to-loop-top
/// <see cref="IntervalMs"/> exactly (a literal tiling of the pump thread's own phases, not a sample), so a consumer can
/// attribute the whole frame. <see cref="GcPauseMs"/> and <see cref="GcCollections"/> are correlation overlays and are
/// not additional tiles. Presentation-side only — Stopwatch/runtime telemetry, never simulation state.
/// </summary>
/// <param name="ProducedFrameIndex">The launcher's produced-frame counter for this sample (the throttle key).</param>
/// <param name="IntervalMs">The loop-top-to-loop-top interval — the delivered frame time.</param>
/// <param name="PumpMs">Loop-top through the input drain (event pump, input, command shell).</param>
/// <param name="ClockMs">Exit/clock sampling and fixed-step accumulator bookkeeping.</param>
/// <param name="InputSnapshotMs">Fixed-step input snapshot construction.</param>
/// <param name="CommandApplyMs">Fixed-step command registry application and dispatch.</param>
/// <param name="SimulationStepMs">The registered fixed-step simulation callback.</param>
/// <param name="FixedStepOverheadMs">Fixed-step loop/context bookkeeping outside the three callbacks.</param>
/// <param name="SimulationOutputMs">Simulation-command output flush and window-size sampling.</param>
/// <param name="GpuDrainMs">The begin-frame wait where the PRIOR frame's GPU work drains.</param>
/// <param name="ProduceMs">The root render node's <c>ProduceFrame</c>.</param>
/// <param name="PresentMs">The swapchain blit + present submit.</param>
/// <param name="PostPresentMs">Post-present exit, timing-feedback, genlock, and deadline bookkeeping.</param>
/// <param name="PacerMs">The display-aware pacer's wait to the frame's deadline.</param>
/// <param name="RemainderMs">The interval minus every named bucket (the untimed slack).</param>
/// <param name="GcPauseMs">CLR-reported process pause time overlapping this frame interval.</param>
/// <param name="GcCollections">Number of generation collection completions overlapping this frame interval.</param>
/// <param name="FixedSteps">Number of fixed simulation ticks consumed by this rendered frame.</param>
/// <param name="SkippedPresentTotal">The presenter's running skipped-present total (a dropped-frame fingerprint).</param>
public readonly record struct FrameTimingSample(
    ulong ProducedFrameIndex,
    double IntervalMs,
    double PumpMs,
    double ClockMs,
    double InputSnapshotMs,
    double CommandApplyMs,
    double SimulationStepMs,
    double FixedStepOverheadMs,
    double SimulationOutputMs,
    double GpuDrainMs,
    double ProduceMs,
    double PresentMs,
    double PostPresentMs,
    double PacerMs,
    double RemainderMs,
    double GcPauseMs,
    int GcCollections,
    ulong FixedSteps,
    ulong SkippedPresentTotal
);
