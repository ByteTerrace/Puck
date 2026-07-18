namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the machine is deterministic. Two independently-built machines, direct-booted from the same synthetic
/// cartridge and advanced the same number of frames, must reach byte-identical whole-machine state. The comparison is
/// the full snapshot — the same coarse hash-then-localize story <see cref="HashDivergenceProbe"/> drives from the CLI:
/// on a mismatch, <see cref="HashDivergenceProbe.DescribeDivergence"/> names the first diverging component (and, for
/// the bus, the memory sub-region) and byte offset instead of a bare "diverged", so a real regression here points
/// straight at the culprit subsystem. This is the foundation every higher tier rests on.
/// </summary>
/// <remarks>The deeper savestate checks live in the sibling <c>state-round-trip</c> (a <c>Snapshot</c>/<c>Restore</c>
/// round-trip is byte-identical) and <c>fork-determinism</c> (a forked sibling stays byte-identical) stages; this stage
/// proves two independent RUNS agree, so a failure here plus green siblings localises to the run rather than the
/// snapshot layer.</remarks>
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

        var firstSnapshot = first.Machine.Snapshot();
        var secondSnapshot = second.Machine.Snapshot();

        if (!firstSnapshot.ContentEquals(other: secondSnapshot)) {
            var detail = HashDivergenceProbe.DescribeDivergence(a: firstSnapshot, b: secondSnapshot);

            return PostStageOutcome.Fail(detail: $"two independent machines diverged after {Frames} frames — {detail}");
        }

        return PostStageOutcome.Pass(detail: $"two independent machines byte-identical after {Frames} frames ({firstSnapshot.Size} state bytes)");
    }
}
