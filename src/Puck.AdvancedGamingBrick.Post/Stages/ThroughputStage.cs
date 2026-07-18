using System.Diagnostics;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: raw throughput. Runs the synthetic cartridge for a fixed span under a stopwatch and reports frames per
/// second, the multiple of real time, and the effective master-cycle rate. It always passes — it is a measurement, not a
/// gate — so the number lands in the report and can be tracked across changes (the before/after that makes a tick-path
/// optimisation a fact rather than a claim).
/// </summary>
internal sealed class ThroughputStage : IPostStage {
    private const int BenchFrames = 200;
    private const int WarmFrames = 15;

    /// <inheritdoc/>
    public string Name =>
        "throughput";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        using var machine = PostMachine.Build(bios: context.BiosImage, rom: SyntheticRom.Create());

        machine.RunFrames(frames: WarmFrames);

        var stopwatch = Stopwatch.StartNew();

        machine.RunFrames(frames: BenchFrames);
        stopwatch.Stop();

        var fps = (BenchFrames / stopwatch.Elapsed.TotalSeconds);
        var realtimeMultiple = (fps / PostMachine.HardwareFps);
        var megaCyclesPerSecond = ((fps * PostMachine.CyclesPerFrame) / 1e6);

        return PostStageOutcome.Pass(
            detail: $"{fps:F0} fps ({realtimeMultiple:F1}x realtime, {megaCyclesPerSecond:F1} Mcycle/s) over {BenchFrames} frames"
        );
    }
}
