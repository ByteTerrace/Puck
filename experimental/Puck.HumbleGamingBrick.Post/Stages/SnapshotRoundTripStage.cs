namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: a snapshot restores exactly. Run to a mid-point and snapshot; run on and capture the resulting state;
/// restore the mid-point snapshot and run the same span again; the two resulting states must be byte-identical. This
/// proves the save-state layer is complete (no live field left unserialized) and faithful — the prerequisite for the
/// mid-frame rewind / netplay the machine is committed to.
/// </summary>
internal sealed class SnapshotRoundTripStage : IPostStage {
    private const int WarmFrames = 200;
    private const int TailFrames = 200;

    /// <inheritdoc/>
    public string Name =>
        "snapshot-round-trip";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        using var machine = PostMachine.Build(model: ConsoleModel.Dmg, rom: SyntheticRom.Create());

        PostMachine.RunFrames(instance: machine, frames: WarmFrames);

        var midpoint = machine.Machine.Snapshot();

        PostMachine.RunFrames(instance: machine, frames: TailFrames);

        var afterFirstRun = machine.Machine.Snapshot();

        machine.Machine.Restore(snapshot: midpoint);

        PostMachine.RunFrames(instance: machine, frames: TailFrames);

        var afterSecondRun = machine.Machine.Snapshot();

        return afterFirstRun.ContentEquals(other: afterSecondRun)
            ? PostStageOutcome.Pass(detail: $"save@{WarmFrames}f, then +{TailFrames}f twice, byte-identical ({midpoint.Size} state bytes)")
            : PostStageOutcome.Fail(detail: $"restored run diverged from the original after {TailFrames} frames");
    }
}
