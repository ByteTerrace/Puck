namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the per-frame hot loop is allocation-free. Warms the machine on the same synthetic cartridge and
/// stepping path <see cref="ThroughputStage"/> uses, takes a <see cref="GC.GetAllocatedBytesForCurrentThread()"/>
/// baseline, then advances a further span of frames and asserts the delta is exactly zero — so a future
/// closure-in-a-tick-path or LINQ-in-a-mapper regression surfaces as a red battery instead of a demo GC spike.
/// </summary>
internal sealed class AllocationStage : IPostStage<PostContext> {
    private const int MeasureFrames = 600;
    private const int WarmFrames = 120;

    /// <inheritdoc/>
    public string Name =>
        "zero-alloc";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        MachineStageProbes.VerifyZeroAllocation(
            build: () => PostMachine.Build(
                bios: context.BiosImage,
                rom: SyntheticRom.Create()
            ),
            measureFrames: MeasureFrames,
            runFrames: static (machine, frames) => machine.RunFrames(frames: frames),
            warmFrames: WarmFrames
        );
}
