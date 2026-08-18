namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the per-frame hot loop is allocation-free. Warms the machine on the same synthetic ROM and stepping
/// path <see cref="ThroughputStage"/> uses, takes a <see cref="GC.GetAllocatedBytesForCurrentThread()"/> baseline, then
/// advances a further span of frames and asserts the delta is exactly zero — so a future closure-in-a-tick-path or
/// LINQ-in-a-mapper regression surfaces as a red battery instead of a demo GC spike.
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
    public PostStageOutcome Run(PostContext context) {
        using var machine = PostMachine.Build(
            model: ConsoleModel.Dmg,
            rom: SyntheticRom.Create()
        );

        PostMachine.RunFrames(
            frames: WarmFrames,
            instance: machine
        );

        var before = GC.GetAllocatedBytesForCurrentThread();

        PostMachine.RunFrames(
            frames: MeasureFrames,
            instance: machine
        );

        var delta = (GC.GetAllocatedBytesForCurrentThread() - before);

        return ((delta == 0)
            ? PostStageOutcome.Pass(detail: $"0 B allocated over {MeasureFrames} frames after {WarmFrames}-frame warm-up")
            : PostStageOutcome.Fail(detail: $"{delta:N0} B allocated over {MeasureFrames} frames after {WarmFrames}-frame warm-up (expected 0)"));
    }
}
