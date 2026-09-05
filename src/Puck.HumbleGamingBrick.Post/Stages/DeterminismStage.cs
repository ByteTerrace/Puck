using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the machine is deterministic. Two independently-built machines, driven from the same synthesized
/// post-boot state by the same synthetic ROM over the same number of frames, must reach byte-identical state. The
/// comparison is over the full snapshot (every component's serialized state), not just the framebuffer, so it catches a
/// divergence anywhere in the machine — the foundation every higher tier and the cross-generation link determinism rest
/// on.
/// </summary>
internal sealed class DeterminismStage : IPostStage<PostContext> {
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

        return MachineStageProbes.VerifyDeterminism<MachineInstance, MachineSnapshot, MachineIdentity, Tick>(
            build: () => PostMachine.Build(
                model: ConsoleModel.DmgC,
                rom: rom
            ),
            describeDivergence: HashDivergenceProbe.DescribeDivergence,
            frames: Frames,
            runFrames: PostMachine.RunFrames,
            snapshot: static instance => instance.Machine.Snapshot()
        );
    }
}
