namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the hand-assembled CPU/PPU/APU/IRQ/DMA/DI smoke vectors. Each vector runs the relevant subsystem in
/// isolation on a flat test bus (or a freshly composed machine) and asserts the resulting state, guarding the
/// highest-risk behaviours — flag derivation, the barrel shifter, the MSR field-mask fix, Thumb entry, the interrupt
/// pipeline, timer overflow, immediate DMA, PPU timing/sprites/affine/blend, APU channels + Direct Sound, BIOS IRQ
/// dispatch, and the DI composition root. It needs no external ROM; the one BIOS-dependent check skips when only the
/// zeroed BIOS stub is present.
/// </summary>
internal sealed class SmokeStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "smoke";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var result = SmokeTests.Run(bios: context.BiosImage);
        var total = (result.Passed + result.Failed);
        var skipNote = ((result.Skipped > 0) ? $", {result.Skipped} skipped (no real BIOS)" : string.Empty);

        return ((result.Failed == 0)
            ? PostStageOutcome.Pass(detail: $"{result.Passed}/{total} CPU/PPU/APU/IRQ/DMA/DI vectors passed{skipNote}")
            : PostStageOutcome.Fail(detail: $"{result.Passed}/{total} passed{skipNote}; failed: {string.Join(separator: ", ", values: result.Failures)}"));
    }
}
