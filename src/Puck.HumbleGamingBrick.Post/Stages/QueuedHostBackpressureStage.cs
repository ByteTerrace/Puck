using Puck.Hosting;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the queued host admits only a finite number of incomplete exact tick/input segments. Sustained producer
/// pressure waits for one completion, reports that backpressure observably, and then accepts the new segment without
/// dropping any previously accepted work. A thin wrapper that exercises the SM83 core through the shared
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
        var result = QueuedHostContractProbe.VerifyBackpressure(
            withContent: () => new MachineHost(model: ConsoleModel.Dmg, cartridgeRom: SyntheticRom.Create())
        );

        return (result.Passed ? PostStageOutcome.Pass(detail: result.Detail) : PostStageOutcome.Fail(detail: result.Detail));
    }
}
