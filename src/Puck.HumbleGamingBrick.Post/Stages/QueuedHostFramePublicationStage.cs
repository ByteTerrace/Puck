using Puck.GamingBricks;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the queued host writes whole frames into an uploaded source's region. An empty host writes its opaque
/// black frame without touching the region's header, an unchanged frame repeats its sequence, a short region is refused,
/// and while the worker completes newer frames every write of one sequence carries the same bytes. A thin wrapper that
/// exercises the SM83 core through the shared <see cref="QueuedHostContractProbe"/> so both batteries gate the one
/// substrate.
/// </summary>
internal sealed class QueuedHostFramePublicationStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public bool IsConcurrent =>
        true;
    /// <inheritdoc/>
    public string Name =>
        "queued-host-frame-publication";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var result = QueuedHostContractProbe.VerifyFramePublication(
            withContent: () => new MachineHost(
                bootMode: MachineBootMode.Fast,
                model: ConsoleModel.DmgC,
                cartridgeRom: SyntheticRom.Create()
            ),
            empty: () => new MachineHost(
                model: ConsoleModel.DmgC,
                bootMode: MachineBootMode.Fast
            )
        );

        return (result.Passed
            ? PostStageOutcome.Pass(detail: result.Detail)
            : PostStageOutcome.Fail(detail: result.Detail)
        );
    }
}
