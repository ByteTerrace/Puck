namespace Puck.HumbleGamingDeck.Post;

/// <summary>The Deck battery's Tier A and Tier B stage registry.</summary>
internal static class PostStages {
    /// <summary>Creates the stages in their execution order.</summary>
    /// <returns>The independent stage instances for one battery run.</returns>
    public static IPostStage<PostContext>[] Create() {
        return [new DeterminismStage(), new AllocationStage(), new ForkDeterminismStage(),
            new SnapshotStage(), new LoaderStage(), new CpuTimingStage(), new ThroughputStage(),
            new EmbeddingStage(), new QueuedHostCheckpointStage(), new QueuedHostBackpressureStage(),
            new QueuedHostFramePublicationStage(), new QueuedHostAudioStage(), new QueuedHostTimeTravelStage(),
            new QueuedHostMemoryAccessStage(), new PictureStage(),
            new ControllerStage(), new OamDmaStage(), new ApuStage(),
            new Nes6502SstStage(), new NestestStage(), new NestestBootStage(), new AccuracyCoinStage()];
    }
}
