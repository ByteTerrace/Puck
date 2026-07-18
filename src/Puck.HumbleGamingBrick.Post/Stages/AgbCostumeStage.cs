using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the Agb costume is a working machine and, for a cartridge that never probes for Advance hardware,
/// indistinguishable from the Cgb costume at the pixels. Two checks: an Agb machine is deterministic against an
/// independently built twin (full-snapshot comparison, same discipline as <see cref="DeterminismStage"/>), and an Agb
/// machine's framebuffer is bit-identical to a Cgb machine's after the same frames of the same non-detecting ROM —
/// the carry-forward rule in miniature. The comparison is framebuffer-only because the boot handoff legitimately
/// differs (the AGB's extra <c>inc b</c>), which a ROM that ignores B never observes.
/// </summary>
internal sealed class AgbCostumeStage : IPostStage {
    private const int Frames = 300;

    /// <inheritdoc/>
    public string Name =>
        "agb-costume";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rom = SyntheticRom.Create();

        using var first = PostMachine.Build(model: ConsoleModel.Agb, rom: rom);
        using var second = PostMachine.Build(model: ConsoleModel.Agb, rom: rom);
        using var cgb = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);

        PostMachine.RunFrames(instance: first, frames: Frames);
        PostMachine.RunFrames(instance: second, frames: Frames);
        PostMachine.RunFrames(instance: cgb, frames: Frames);

        if (!first.Machine.Snapshot().ContentEquals(other: second.Machine.Snapshot())) {
            return PostStageOutcome.Fail(detail: $"two independent Agb machines diverged after {Frames} frames");
        }

        var agbPixels = first.GetRequiredService<IFramebuffer>().Pixels;
        var cgbPixels = cgb.GetRequiredService<IFramebuffer>().Pixels;

        return (agbPixels.SequenceEqual(other: cgbPixels)
            ? PostStageOutcome.Pass(detail: $"Agb deterministic and pixel-identical to Cgb after {Frames} frames of a non-detecting ROM")
            : PostStageOutcome.Fail(detail: $"Agb framebuffer diverged from Cgb after {Frames} frames of a non-detecting ROM"));
    }
}
