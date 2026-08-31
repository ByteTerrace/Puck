using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: a fork diverges identically. Fork a running machine (a sibling loaded from the parent's current state),
/// then advance both the parent and the fork the same number of frames; they must reach byte-identical state. This
/// exercises the same fork seam that a two-machine link co-simulation and rollback rely on — an independent machine from
/// a common point that stays in lock-step under identical input. The stale-handle lifecycle checks ride along in
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
        using var parent = PostMachine.Build(
            model: ConsoleModel.Dmg,
            rom: SyntheticRom.Create()
        );

        return MachineStageProbes.VerifyForkLifecycle<Machine, MachineConfiguration, MachineSnapshot, MachineIdentity, Tick>(
            describeDivergence: HashDivergenceProbe.DescribeDivergence,
            parent: parent,
            runFrames: PostMachine.RunFrames,
            snapshot: static machine => machine.Snapshot(),
            tailFrames: TailFrames,
            warmFrames: WarmFrames
        );
    }
}
