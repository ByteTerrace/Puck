namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: a fork diverges identically. Fork a running machine (a sibling loaded from the parent's current
/// whole-machine state), then advance both the parent and the fork the same number of frames; they must reach
/// byte-identical state. This exercises the same fork seam a two-machine link co-simulation and rollback rely on — an
/// independent machine from a common point that stays in lock-step under identical input. Like <c>determinism</c>,
/// it compares the entire snapshot image, and a mismatch is localized to the diverging component and byte offset
/// via <see cref="HashDivergenceProbe.DescribeDivergence"/>. The stale-handle lifecycle checks ride along in
/// <see cref="MachineStageProbes"/>' shared fork-lifecycle probe.
/// </summary>
internal sealed class ForkDeterminismStage : IPostStage<PostContext> {
    private const int TailFrames = 200;
    private const int WarmFrames = 200;

    /// <inheritdoc/>
    public string Name =>
        "fork-determinism";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        using var parent = PostMachine.BuildInstance(
            bios: context.BiosImage,
            rom: SyntheticRom.Create()
        );

        return MachineStageProbes.VerifyForkLifecycle<AdvancedGamingBrickMachine, AgbMachineConfiguration, AgbMachineSnapshot, AgbMachineIdentity, long>(
            describeDivergence: HashDivergenceProbe.DescribeDivergence,
            parent: parent,
            runFrames: PostMachine.RunFrames,
            snapshot: static machine => machine.Snapshot(),
            tailFrames: TailFrames,
            warmFrames: WarmFrames
        );
    }
}
