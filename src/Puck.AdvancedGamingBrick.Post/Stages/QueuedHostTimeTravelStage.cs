using Puck.Hosting;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the machine-neutral time-travel contract over the Advanced (ARM7TDMI) queued host — rewind lands the
/// true past frame and restores the host tick-to-cycle accumulator phase (a restored instant plus identical future ticks
/// buys identical budgets), the runahead lookahead holds its native-frame lead over a long mismatched-cadence horizon and
/// under fast-forward without perturbing the authority, an over-cap fast-forward factor clamps instead of faulting the
/// worker, and a rewind clears the abandoned future's audio. A thin wrapper that drives the Advanced core through the
/// shared <see cref="QueuedHostContractProbe"/>; the Advanced host exposes no debug peek, so the authority fingerprint is
/// the emitted light of its backdrop-walking synthetic ROM — a monotone function of the cycles run, so a stale
/// accumulator phase (a different cycle budget) folds to a different value.
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
        ArgumentNullException.ThrowIfNull(argument: context);

        var bios = context.BiosImage.ToArray();
        var result = QueuedHostContractProbe.VerifyTimeTravel(
            withContent: () => new AdvancedMachineHost(cartridgeRom: SyntheticRom.Create(), biosImage: bios),
            withAudio: () => new AdvancedMachineHost(cartridgeRom: SyntheticRom.Create(), biosImage: bios, audioSampleRate: RequestedSampleRate),
            observe: ObserveState
        );

        return (result.Passed ? PostStageOutcome.Pass(detail: result.Detail) : PostStageOutcome.Fail(detail: result.Detail));
    }

    // The emitted light of the synthetic ROM's uniform backdrop (its palette-entry-0 walk), folded to a long. The
    // backdrop colour is a monotone function of the CPU iterations the machine ran, so it reflects the exact cycle
    // position: a rewind that restored the wrong tick-to-cycle phase lands the authority on a different cycle total and
    // this fold diverges.
    private static long ObserveState(AdvancedMachineHost host) {
        var light = host.EmittedLight;

        return (((long)BitConverter.SingleToUInt32Bits(value: light.X) << 32)
            ^ ((long)BitConverter.SingleToUInt32Bits(value: light.Y) << 16)
            ^ BitConverter.SingleToUInt32Bits(value: light.Z));
    }
}
