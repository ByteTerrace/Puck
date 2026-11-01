namespace Puck.HumbleGamingDeck.Post;

/// <summary>Drives the synchronous core without a worker, renderer, or audio device through the shared embedding
/// probe.</summary>
internal sealed class EmbeddingStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "embedding";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (BatterySaveProbe.Verify(artifactsDirectory: context.ArtifactsDirectory) is { } failure) {
            return failure;
        }
        using var core = new HumbleGamingDeckCore(cartridgeImage: PostMachine.CreateImage());

        core.ConfigureAudio(sampleRate: 48_000);
        byte[] state = [];
        var length = core.CaptureState(buffer: ref state);

        core.RunCycles(cycles: 120_000);
        core.RestoreState(buffer: state, length: length);
        Span<short> samples = stackalloc short[2];

        if (core.DrainAudioSamples(destination: samples) != 0) {
            return PostStageOutcome.Fail(detail: "restoring the core retained audio from the abandoned future");
        }

        // Two NTSC frames of master ticks, so the budget spans at least one completed picture.
        return CoreEmbeddingProbe.Verify(
            core: core,
            cycleBudget: (2L * 357_366L),
            framebufferLength: (DeckMachineHost.ScreenWidth * DeckMachineHost.ScreenHeight)
        );
    }
}
