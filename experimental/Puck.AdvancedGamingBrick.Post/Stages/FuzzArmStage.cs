namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-B stage: run the FuzzARM randomized-instruction ROMs (found in a sibling <c>FuzzARM</c> directory beside the
/// gba-tests corpus) and pass only when none dumps a failure marker to EWRAM. FuzzARM stresses the ARM/Thumb decoders
/// with randomized operand patterns the hand-written suites miss. The group skips when the directory is absent; a failure
/// lists exactly which ROMs failed.
/// </summary>
internal sealed class FuzzArmStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "fuzzarm";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = RomCatalog.FuzzArm(root: context.TestRomRoot);

        if (cases.Count == 0) {
            return PostStageOutcome.Skip(detail: "no FuzzARM ROMs beside the corpus (clone github.com/DenSinH/FuzzARM)");
        }

        var failures = new List<string>();
        var passed = 0;

        foreach (var romCase in cases) {
            try {
                var (pass, detail) = FuzzArmProbe.Run(romCase: romCase, bios: context.BiosImage);

                if (pass) {
                    ++passed;
                } else {
                    failures.Add(item: $"{romCase.Name} ({detail})");
                }
            } catch (Exception exception) {
                failures.Add(item: $"{romCase.Name} (threw {exception.GetType().Name}: {exception.Message})");
            }
        }

        return (failures.Count == 0)
            ? PostStageOutcome.Pass(detail: $"{passed}/{cases.Count} passed")
            : PostStageOutcome.Fail(detail: $"{passed}/{cases.Count} passed; failed: {string.Join(separator: ", ", values: failures)}");
    }
}
