namespace Puck.HumbleGamingDeck.Post;

/// <summary>Verifies that steady CPU and bus execution allocates no managed memory.</summary>
internal sealed class AllocationStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "zero-alloc";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return MachineStageProbes.VerifyZeroAllocation(build: PostMachine.Build, measureFrames: 100,
            runFrames: PostMachine.RunBatches, warmFrames: 30);
    }
}
