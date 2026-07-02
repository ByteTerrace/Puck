namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the machine is deterministic. Two independently-built machines, driven from the same synthesized
/// post-boot state by the same synthetic ROM over the same number of frames, must reach byte-identical state. The
/// comparison is over the full snapshot (every component's serialized state), not just the framebuffer, so it catches a
/// divergence anywhere in the machine — the foundation every higher tier and the cross-generation link determinism rest
/// on.
/// </summary>
internal sealed class DeterminismStage : IPostStage {
    private const int Frames = 300;

    /// <inheritdoc/>
    public string Name =>
        "determinism";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rom = SyntheticRom.Create();

        using var first = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);
        using var second = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);

        PostMachine.RunFrames(instance: first, frames: Frames);
        PostMachine.RunFrames(instance: second, frames: Frames);

        var firstState = first.Machine.Snapshot();
        var secondState = second.Machine.Snapshot();

        return firstState.ContentEquals(other: secondState)
            ? PostStageOutcome.Pass(detail: $"two independent machines byte-identical after {Frames} frames ({firstState.Size} state bytes)")
            : PostStageOutcome.Fail(detail: $"two independent machines diverged after {Frames} frames");
    }
}
