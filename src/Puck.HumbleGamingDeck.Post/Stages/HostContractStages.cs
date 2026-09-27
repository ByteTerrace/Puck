using Puck.Machines;
using Puck.Maths;

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
        using var core = new HumbleGamingDeckCore(cartridgeImage: PostMachine.CreateImage());

        // Two NTSC frames of master ticks, so the budget spans at least one completed picture.
        return CoreEmbeddingProbe.Verify(
            core: core,
            cycleBudget: (2L * 357_366L),
            framebufferLength: (DeckMachineHost.ScreenWidth * DeckMachineHost.ScreenHeight)
        );
    }
}
/// <summary>Proves the durable checkpoint through the queued host: capture, corruption and identity refusal, import, and
/// matching continuation.</summary>
internal sealed class QueuedHostCheckpointStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "queued-host-checkpoint";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        HostContract.Outcome(result: QueuedHostContractProbe.VerifyCheckpoint(withContent: HostContract.WithContent));
}
/// <summary>Proves the bounded queue backpressures and completes every accepted segment.</summary>
internal sealed class QueuedHostBackpressureStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "queued-host-backpressure";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        HostContract.Outcome(result: QueuedHostContractProbe.VerifyBackpressure(withContent: HostContract.WithContent));
}
/// <summary>Proves frames reach an uploaded source whole and in order while the worker keeps completing frames.</summary>
internal sealed class QueuedHostFramePublicationStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "queued-host-frames";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        HostContract.Outcome(result: QueuedHostContractProbe.VerifyFramePublication(
            empty: static () => new DeckMachineHost(),
            withContent: HostContract.WithContent
        ));
}
/// <summary>Proves audio synthesis is gated on attachment: none while detached, samples at the requested rate once
/// attached.</summary>
internal sealed class QueuedHostAudioStage : IPostStage<PostContext> {
    private const int RequestedSampleRate = 32_000;

    /// <inheritdoc/>
    public string Name => "queued-host-audio";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        HostContract.Outcome(result: QueuedHostContractProbe.VerifyAudio(
            attached: static () => new DeckMachineHost(
                audioSampleRate: RequestedSampleRate,
                cartridgeImage: PostMachine.CreateImage()
            ),
            detached: HostContract.WithContent,
            requestedRate: RequestedSampleRate
        ));
}
/// <summary>Proves rewind, runahead and fast-forward over the queued host, observing the authoritative machine through
/// its work RAM.</summary>
internal sealed class QueuedHostTimeTravelStage : IPostStage<PostContext> {
    private const int RequestedSampleRate = 32_000;

    /// <inheritdoc/>
    public string Name => "queued-host-time-travel";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        HostContract.Outcome(result: QueuedHostContractProbe.VerifyTimeTravel(
            observe: HostContract.ObserveWorkRam,
            withAudio: static () => new DeckMachineHost(
                audioSampleRate: RequestedSampleRate,
                cartridgeImage: PostMachine.CreateImage()
            ),
            withContent: HostContract.WithContent
        ));
}
/// <summary>Proves the debug memory window is marshalled through the worker: reproducible pokes, side-effect-free peeks
/// under concurrency, and coherent round trips while segments stream.</summary>
internal sealed class QueuedHostMemoryAccessStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "queued-host-memory";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        HostContract.Outcome(result: QueuedHostContractProbe.VerifyConcurrentMemoryAccess(
            regionLength: 0x800,
            regionStart: 0x0000,
            // The fixture program writes only $0000 and $6000, so work RAM at $0300 is the probe's to poke.
            scratchAddress: 0x0300,
            withContent: HostContract.WithContent
        ));
}
/// <summary>The fixture host and observation the host-contract stages share.</summary>
internal static class HostContract {
    /// <summary>Builds a host over the fixture cartridge with no audio.</summary>
    /// <returns>The assigned host, owned by the caller.</returns>
    public static DeckMachineHost WithContent() =>
        new(cartridgeImage: PostMachine.CreateImage());
    /// <summary>Folds the host's work RAM, read through its side-effect-free debug window, into a fingerprint.</summary>
    /// <param name="host">The host to observe.</param>
    /// <returns>The FNV-1a fold of all 2 KiB of work RAM.</returns>
    public static long ObserveWorkRam(DeckMachineHost host) {
        Span<byte> ram = stackalloc byte[0x800];

        host.PeekBytes(
            address: 0x0000,
            destination: ram
        );

        var hash = Fnv1aHash.Create();

        foreach (var value in ram) {
            hash.Add(value: value);
        }

        return unchecked((long)hash.Value);
    }
    /// <summary>Maps a shared probe's result to a stage outcome.</summary>
    /// <param name="result">The probe's result.</param>
    /// <returns>A pass or fail carrying the probe's detail.</returns>
    public static PostStageOutcome Outcome(QueuedHostProbeResult result) =>
        (result.Passed ? PostStageOutcome.Pass(detail: result.Detail) : PostStageOutcome.Fail(detail: result.Detail));
}
