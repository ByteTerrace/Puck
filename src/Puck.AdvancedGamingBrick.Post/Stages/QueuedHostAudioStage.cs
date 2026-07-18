using Puck.Hosting;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the neutral audio capability is gated strictly on host attachment — a host built with no audio
/// consumer reports rate 0 and yields zero samples no matter how long it runs, while a host built WITH one reports
/// the requested rate and yields samples drained off the emulation thread through the worker's own ring. A thin
/// wrapper that exercises the Advanced core through the shared <see cref="QueuedHostContractProbe"/> so both
/// batteries gate the one substrate.
/// </summary>
internal sealed class QueuedHostAudioStage : IPostStage {
    private const int RequestedSampleRate = 32_000;

    /// <inheritdoc/>
    public string Name =>
        "queued-host-audio";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var bios = context.BiosImage.ToArray();
        var result = QueuedHostContractProbe.VerifyAudio(
            attached: () => new AdvancedMachineHost(cartridgeRom: SyntheticRom.Create(), biosImage: bios, audioSampleRate: RequestedSampleRate),
            detached: () => new AdvancedMachineHost(cartridgeRom: SyntheticRom.Create(), biosImage: bios),
            requestedRate: RequestedSampleRate
        );

        return (result.Passed ? PostStageOutcome.Pass(detail: result.Detail) : PostStageOutcome.Fail(detail: result.Detail));
    }
}
