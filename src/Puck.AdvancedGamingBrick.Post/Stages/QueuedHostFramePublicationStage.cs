using Puck.Hosting;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: a blocked CPU-to-GPU upload leases an immutable complete frame without holding the worker's frame
/// publication lock. Later native frames continue to complete, concurrent publish calls serialize, and device loss or
/// disposal waits until the uploader has finished consuming its caller-owned pixels. A thin wrapper that exercises the
/// Advanced core through the shared <see cref="QueuedHostContractProbe"/> so both batteries gate the one substrate.
/// </summary>
internal sealed class QueuedHostFramePublicationStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "queued-host-frame-publication";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var bios = context.BiosImage.ToArray();
        var result = QueuedHostContractProbe.VerifyFramePublication(
            withContent: () => new AdvancedMachineHost(cartridgeRom: SyntheticRom.Create(), biosImage: bios),
            empty: () => new AdvancedMachineHost()
        );

        return (result.Passed ? PostStageOutcome.Pass(detail: result.Detail) : PostStageOutcome.Fail(detail: result.Detail));
    }
}
