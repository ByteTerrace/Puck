using Puck.Machines;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>Compares snapshots from independent machines executing the same master-tick budgets.</summary>
internal sealed class DeterminismStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "determinism";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return MachineStageProbes.VerifyDeterminism<MachineInstance<HgdMachine, HgdMachineConfiguration>, HgdMachineSnapshot, HgdMachineIdentity, ulong>(
            build: PostMachine.Build, runFrames: PostMachine.RunBatches,
            snapshot: static instance => instance.Machine.Snapshot(), frames: 100, describeDivergence: PostMachine.Describe);
    }
}
