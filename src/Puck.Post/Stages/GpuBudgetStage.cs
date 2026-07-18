using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-D stage D4. The GPU-ms budget. It times the REAL hero world render — the full beam → cull-args → views
/// (indirect) → composite pipeline through <see cref="Puck.SdfVm.SdfWorldEngine"/> with opt-in GPU timestamp bracketing — on
/// the offscreen Vulkan host, proving the per-pass timestamp counters (the GPU-timing plumbing:
/// <see cref="IGpuTimingPoolFactory"/> / <see cref="IGpuTimingPool"/> / <see cref="IGpuTimingRecorder"/>) are LIVE and
/// resolve to a plausible per-frame GPU time, then applies a LOOSE sanity ceiling. Without a calibrated per-machine
/// baseline a tight budget is not yet possible (per the plan), so this is a regression TRIPWIRE — the hero frame
/// ballooning past the generous ceiling means the GPU time regressed catastrophically — plus end-to-end proof that the
/// counters convert ticks to real milliseconds. It renders two frames (the first pays the one-time source-layout
/// transitions; the second is the steady-state cost the budget reads). Runs in-process on the Vulkan host (same-device,
/// no child probe), grouped in Tier D as the plan's performance-validation item, ahead of the destabilizing
/// device-loss / hot-switch probes so it measures a healthy device. Skips (never fails) when the device reports no
/// timestamp support.
/// </summary>
internal sealed class GpuBudgetStage : IPostStage {
    private const double LooseCeilingMs = 100.0; // a catastrophic-regression guard, NOT a calibrated per-machine budget
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "gpu-budget";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.D;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var device = context.RequireGpuDevice();
        var timingFactory = context.Resolve<IGpuTimingPoolFactory>();

        if (!timingFactory.GetCapabilities(deviceContext: device).IsSupported) {
            return PostStageOutcome.Skip(detail: "the device reports no GPU timestamp support (IGpuTimingPoolFactory.GetCapabilities.IsSupported is false)");
        }

        var gpu = context.Resolve<IGpuComputeServices>();
        var timingRecorder = context.Resolve<IGpuTimingRecorder>();
        var program = WorldStage.BuildHeroScene();
        var frame = WorldStage.BuildHeroFrame(program: program, width: WorldWidth, height: WorldHeight);

        // NOTE: the timing bracket is the engine's four per-pass marks — frame-start (top of pipe) → composite-close
        // (bottom of pipe) — so the final output transition + query resolve fall just outside the measurement;
        // negligible against the sanity ceiling.
        using var renderer = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
            options: new SdfWorldEngineOptions(Program: program, TimingFactory: timingFactory, TimingRecorder: timingRecorder),
            width: WorldWidth
        );

        // Two frames: the first pays the one-time layout transitions (sources come up from Undefined); the second is
        // the steady-state cost the budget reads.
        _ = renderer.RenderFrame(frame: frame);
        _ = renderer.RenderFrame(frame: frame);

        if (renderer.LastFrameGpuMilliseconds is not double elapsedMs) {
            return PostStageOutcome.Infra(detail: "GPU timing was enabled but no timestamp result was read back after a completed submit — the timing plumbing is broken");
        }

        if (elapsedMs <= 0.0) {
            return PostStageOutcome.Fail(detail: $"the GPU timestamp counters resolved to a non-positive frame time ({elapsedMs:0.###} ms) — the counters are not live");
        }

        if (elapsedMs > LooseCeilingMs) {
            return PostStageOutcome.Fail(detail: $"the {WorldWidth}x{WorldHeight} hero world render took {elapsedMs:0.###} ms GPU time, over the {LooseCeilingMs:0} ms sanity ceiling — a catastrophic GPU-time regression");
        }

        return PostStageOutcome.Pass(detail: $"{WorldWidth}x{WorldHeight} hero world render GPU time {elapsedMs:0.###} ms — per-pass counters live and within the {LooseCeilingMs:0} ms sanity ceiling (a loose regression guard pending per-machine calibration)");
    }
}
