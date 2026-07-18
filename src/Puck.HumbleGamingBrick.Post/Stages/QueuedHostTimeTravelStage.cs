using Puck.Hosting;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the machine-neutral time-travel contract over the SM83 queued host — rewind lands the true past frame
/// and restores the host tick-to-cycle accumulator phase (a restored instant plus identical future ticks buys identical
/// budgets), the runahead lookahead holds its native-frame lead over a long mismatched-cadence horizon and under
/// fast-forward without perturbing the authority, an over-cap fast-forward factor clamps instead of faulting the worker,
/// and a rewind clears the abandoned future's audio. A thin wrapper that drives the SM83 core through the shared
/// <see cref="QueuedHostContractProbe"/>; the authority fingerprint folds the machine's I/O + high-RAM window (DIV/timer/
/// PPU registers included) through the host's own debug peek, so a stale accumulator phase shows as a divergence.
/// </summary>
internal sealed class QueuedHostTimeTravelStage : IPostStage {
    private const int RequestedSampleRate = 32_000;

    /// <inheritdoc/>
    public string Name =>
        "queued-host-time-travel";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var result = QueuedHostContractProbe.VerifyTimeTravel(
            withContent: () => new MachineHost(model: ConsoleModel.Dmg, cartridgeRom: SyntheticRom.Create()),
            withAudio: () => new MachineHost(model: ConsoleModel.Dmg, cartridgeRom: SyntheticRom.Create(), audioSampleRate: RequestedSampleRate),
            observe: ObserveState
        );

        return (result.Passed ? PostStageOutcome.Pass(detail: result.Detail) : PostStageOutcome.Fail(detail: result.Detail));
    }

    // An FNV-1a fold of the whole I/O + high-RAM window (0xFF00-0xFFFF) through the host's side-effect-free debug peek —
    // the joypad register, DIV/TIMA timers, PPU LCDC/STAT/LY, interrupt flags, and HRAM. Every byte is a pure function of
    // the cycles run, so a rewind that restored the wrong tick-to-cycle phase (a different cycle budget) folds to a
    // different value.
    private static long ObserveState(MachineHost host) {
        const ulong offsetBasis = 0xCBF29CE484222325ul;
        const ulong prime = 0x100000001B3ul;

        var hash = offsetBasis;

        for (var address = 0xFF00; (address <= 0xFFFF); ++address) {
            hash = ((hash ^ host.PeekByte(address: address)) * prime);
        }

        return unchecked((long)hash);
    }
}
