namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: raw throughput. Runs the synthetic cartridge for a fixed span under a stopwatch and reports frames per
/// second, the multiple of real time, and the effective master-cycle rate. It always passes — it is a measurement, not a
/// gate — so the number lands in the report and can be tracked across changes (the before/after that makes a tick-path
/// optimisation a fact rather than a claim).
/// </summary>
internal sealed class ThroughputStage : IPostStage<PostContext> {
    private const int BenchFrames = 200;
    private const int WarmFrames = 15;

    /// <inheritdoc/>
    public string Name =>
        "throughput";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        MachineStageProbes.MeasureThroughput(
            benchFrames: BenchFrames,
            build: () => PostMachine.Build(
                bios: context.BiosImage,
                rom: SyntheticRom.Create()
            ),
            cycleUnit: "Mcycle/s",
            cyclesPerFrame: PostMachine.CyclesPerFrame,
            hardwareFps: PostMachine.HardwareFps,
            runFrames: static (machine, frames) => machine.RunFrames(frames: frames),
            warmFrames: WarmFrames
        );
}
