using System.Diagnostics;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: raw throughput. Runs the synthetic ROM for a fixed span under a stopwatch and reports frames per second,
/// the multiple of real time, and millions of T-cycles per second. It always passes — it is a measurement, not a gate —
/// so the number lands in the report and can be tracked across changes (the before/after that makes a tick-path
/// optimisation a fact rather than a claim).
/// </summary>
internal sealed class ThroughputStage : IPostStage {
    private const int BenchFrames = 2_000;
    private const int WarmFrames = 60;

    /// <inheritdoc/>
    public string Name =>
        "throughput";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        using var machine = PostMachine.Build(model: ConsoleModel.Dmg, rom: SyntheticRom.Create());

        PostMachine.RunFrames(instance: machine, frames: WarmFrames);

        var stopwatch = Stopwatch.StartNew();

        PostMachine.RunFrames(instance: machine, frames: BenchFrames);
        stopwatch.Stop();

        var fps = (BenchFrames / stopwatch.Elapsed.TotalSeconds);
        var realtimeMultiple = (fps / PostMachine.HardwareFps);
        var megaTCyclesPerSecond = ((fps * PostMachine.TCyclesPerFrame) / 1e6);

        return PostStageOutcome.Pass(
            detail: $"{fps:F0} fps ({realtimeMultiple:F1}x realtime, {megaTCyclesPerSecond:F1} MT/s) over {BenchFrames} frames"
        );
    }
}
