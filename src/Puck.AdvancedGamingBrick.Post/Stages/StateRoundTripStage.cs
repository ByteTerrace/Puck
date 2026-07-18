namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the whole-machine savestate round-trips. Over each generated micro-ROM, the machine is booted,
/// snapshotted at a frame boundary and again mid-frame, restored, and re-run — asserting the framebuffer + register
/// recordings are bit-identical, plus the double-restore idempotence invariant (a thin wrapper over
/// <see cref="StateRoundTripProbe"/>, the same three checks the <c>--state-roundtrip</c> diagnostic runs). It is the
/// machine snapshot-round-trip stage for <c>Snapshot</c>/<c>Restore</c>;
/// any divergence is a genuine hole in the state coverage, since the core is fully deterministic. The micro-ROMs are
/// direct-boot and BIOS-independent, so this runs on the zeroed stub as well as a real BIOS.
/// </summary>
internal sealed class StateRoundTripStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "state-round-trip";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var failures = 0;
        var total = 0;

        foreach (var kind in MicroRoms.Kinds) {
            ++total;

            var (pass, _) = StateRoundTripProbe.Run(rom: MicroRoms.GenerateBytes(kind: kind), label: $"micro:{kind}", bios: context.BiosImage);

            if (!pass) {
                ++failures;
            }
        }

        return ((failures == 0)
            ? PostStageOutcome.Pass(detail: $"all {total} micro-ROMs snapshot/restore byte-identical (frame-boundary, mid-frame, double-restore)")
            : PostStageOutcome.Fail(detail: $"{failures}/{total} micro-ROM(s) diverged after a savestate round-trip"));
    }
}
