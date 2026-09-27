namespace Puck.HumbleGamingDeck.Post;

/// <summary>The Deck battery's Tier A and Tier B stage registry.</summary>
internal static class PostStages {
    /// <summary>Creates the stages in their execution order.</summary>
    /// <returns>The independent stage instances for one battery run.</returns>
    public static IPostStage<PostContext>[] Create() {
        return [new DeterminismStage(), new AllocationStage(), new ForkDeterminismStage(),
            new SnapshotStage(), new LoaderStage(), new CpuTimingStage(), new ThroughputStage(),
            new Nes6502SstStage(), new NestestStage()];
    }
}
