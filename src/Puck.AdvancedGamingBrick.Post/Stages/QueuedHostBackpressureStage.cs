using Puck.Hosting;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the queued host admits only a finite number of incomplete exact tick/input segments. Sustained producer
/// pressure waits for one completion, reports that backpressure observably, and then accepts the new segment without
/// dropping any previously accepted work. A thin wrapper that exercises the Advanced core through the shared
/// <see cref="QueuedHostContractProbe"/> so both batteries gate the one substrate.
/// </summary>
internal sealed class QueuedHostBackpressureStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "queued-host-backpressure";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var bios = context.BiosImage.ToArray();
        var result = QueuedHostContractProbe.VerifyBackpressure(
            withContent: () => new AdvancedMachineHost(cartridgeRom: SyntheticRom.Create(), biosImage: bios)
        );

        return (result.Passed ? PostStageOutcome.Pass(detail: result.Detail) : PostStageOutcome.Fail(detail: result.Detail));
    }
}
