namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the machine is deterministic. Two independently-built machines, direct-booted from the same synthetic
/// cartridge and advanced the same number of frames, must reach identical state. The GBA core exposes no whole-machine
/// snapshot, so the comparison is over the observable state — the full CPU register file (r0..r15 + CPSR) and the
/// rendered framebuffer — which together cover the CPU, bus, timers, and PPU. This is the foundation every higher tier
/// rests on.
/// </summary>
/// <remarks>The core has no <c>Snapshot</c>/<c>Restore</c>/<c>Fork</c> API, so — unlike the Game Boy / Game Boy Color
/// POST — there are no separate snapshot-round-trip and fork-determinism stages; adding a save-state layer to the core
/// would let those be reinstated.</remarks>
internal sealed class DeterminismStage : IPostStage {
    private const int Frames = 200;

    /// <inheritdoc/>
    public string Name =>
        "determinism";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rom = SyntheticRom.Create();

        using var first = PostMachine.Build(bios: context.BiosImage, rom: rom);
        using var second = PostMachine.Build(bios: context.BiosImage, rom: rom);

        first.RunFrames(frames: Frames);
        second.RunFrames(frames: Frames);

        for (var register = 0; (register < 16); ++register) {
            if (first.Machine.Cpu.GetRegister(index: register) != second.Machine.Cpu.GetRegister(index: register)) {
                return PostStageOutcome.Fail(detail: $"CPU r{register} diverged after {Frames} frames");
            }
        }

        if (first.Machine.Cpu.Cpsr != second.Machine.Cpu.Cpsr) {
            return PostStageOutcome.Fail(detail: $"CPSR diverged after {Frames} frames");
        }

        if (!first.Machine.Framebuffer.SequenceEqual(other: second.Machine.Framebuffer)) {
            return PostStageOutcome.Fail(detail: $"framebuffer diverged after {Frames} frames");
        }

        return PostStageOutcome.Pass(detail: $"two independent machines register- and frame-identical after {Frames} frames");
    }
}
