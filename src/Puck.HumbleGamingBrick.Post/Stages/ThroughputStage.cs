namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: raw throughput. Runs the synthetic ROM for a fixed span under a stopwatch and reports frames per second,
/// the multiple of real time, and millions of T-cycles per second. It always passes — it is a measurement, not a gate —
/// so the number lands in the report and can be tracked across changes (the before/after that makes a tick-path
/// optimisation a fact rather than a claim).
/// </summary>
internal sealed class ThroughputStage : IPostStage<PostContext> {
    private const int BenchFrames = 2_000;
    private const int WarmFrames = 60;

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
            build: static () => PostMachine.Build(
                model: ConsoleModel.DmgC,
                rom: SyntheticRom.Create()
            ),
            cycleUnit: "MT/s",
            cyclesPerFrame: PostMachine.TCyclesPerFrame,
            hardwareFps: PostMachine.HardwareFps,
            runFrames: PostMachine.RunFrames,
            warmFrames: WarmFrames
        );
}
