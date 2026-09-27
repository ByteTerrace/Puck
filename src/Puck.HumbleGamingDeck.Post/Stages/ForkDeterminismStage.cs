namespace Puck.HumbleGamingDeck.Post;

/// <summary>Verifies fork replay, isolation, and disposal through the shared machine lifecycle.</summary>
internal sealed class ForkDeterminismStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "fork-lifecycle";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        using var parent = PostMachine.Build();

        return MachineStageProbes.VerifyForkLifecycle<HgdMachine, HgdMachineConfiguration, HgdMachineSnapshot, HgdMachineIdentity, ulong>(
            parent: parent, runFrames: static (machine, count) => machine.RunCycles(masterTicks: (((ulong)count) * PostMachine.BatchTicks)),
            snapshot: static machine => machine.Snapshot(), warmFrames: 20, tailFrames: 30, describeDivergence: PostMachine.Describe);
    }
}
