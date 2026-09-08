namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-B stage: run the ARM/Thumb fuzz-corpus ROMs and pass only when none dumps a failure marker to EWRAM. The fuzz
/// corpus stresses the ARM/Thumb decoders with randomized operand patterns the hand-written suites miss. The group
/// skips when the corpus is absent; every ROM is its own case row, and the ROMs run on every processor at once.
/// </summary>
internal sealed class ArmFuzzStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name =>
        "arm-fuzz";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = RomCatalog.ArmFuzz(root: context.FuzzRoot);

        if (cases.Count == 0) {
            return PostStageOutcome.Skip(detail: "no fuzz-corpus ROMs (fetch the fuzzarm corpus or pass --fuzz)");
        }

        return RomCaseRunner.Run(
            cases: cases,
            parallelism: context.Parallelism,
            probe: romCase => ArmFuzzProbe.Run(
                bios: context.BiosImage,
                romCase: romCase
            )
        );
    }
}
