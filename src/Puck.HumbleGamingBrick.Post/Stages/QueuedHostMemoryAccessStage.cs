using Puck.Hosting;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the host's debug memory window (<see cref="Puck.Abstractions.Machines.IMachineMemoryPeek"/>) is marshaled
/// through the worker, not read off-thread while the worker advances the core. The shared
/// <see cref="QueuedHostContractProbe"/> proves the marshaled poke path is deterministic, that cross-thread peek hammering
/// leaves state replay-identical, and that concurrent poke/peek round-trips against a running machine stay coherent and
/// never fault the queue. The scratch address (<c>0xC200</c>) is a work-RAM byte the synthetic ROM never writes; its
/// deterministic WRAM-fill page (<c>0xC000</c>–<c>0xC0FF</c>) is the compared reproducibility region.
/// </summary>
internal sealed class QueuedHostMemoryAccessStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "queued-host-memory-access";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var result = QueuedHostContractProbe.VerifyConcurrentMemoryAccess(
            withContent: () => new MachineHost(model: ConsoleModel.Dmg, cartridgeRom: SyntheticRom.Create()),
            scratchAddress: 0xC200,
            regionStart: 0xC000,
            regionLength: 0x0100
        );

        return (result.Passed ? PostStageOutcome.Pass(detail: result.Detail) : PostStageOutcome.Fail(detail: result.Detail));
    }
}
