namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: a fork diverges identically. Fork a running machine (a sibling loaded from the parent's current state),
/// then advance both the parent and the fork the same number of frames; they must reach byte-identical state. This
/// exercises the same fork seam that a two-machine link co-simulation and rollback rely on — an independent machine from
/// a common point that stays in lock-step under identical input.
/// </summary>
internal sealed class ForkDeterminismStage : IPostStage {
    private const int WarmFrames = 200;
    private const int TailFrames = 200;

    /// <inheritdoc/>
    public string Name =>
        "fork-determinism";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        using var parent = PostMachine.Build(model: ConsoleModel.Dmg, rom: SyntheticRom.Create());

        PostMachine.RunFrames(instance: parent, frames: WarmFrames);

        using var fork = parent.Fork();

        PostMachine.RunFrames(instance: parent, frames: TailFrames);
        PostMachine.RunFrames(instance: fork, frames: TailFrames);

        var parentState = parent.Machine.Snapshot();
        var forkState = fork.Machine.Snapshot();

        return parentState.ContentEquals(other: forkState)
            ? PostStageOutcome.Pass(detail: $"parent and fork byte-identical after +{TailFrames}f from a common point ({parentState.Size} state bytes)")
            : PostStageOutcome.Fail(detail: $"fork diverged from the parent after {TailFrames} frames");
    }
}
