namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-B stage: run a directory of mooneye acceptance ROMs and pass only when every eligible (ROM, model) reports the
/// Fibonacci pass signature over serial. mooneye is a large, precise timing suite (timer/DIV, PPU timing, interrupts,
/// serial, OAM-DMA), so this is where sub-instruction timing accuracy is held to an external oracle. The group skips
/// when the corpus is absent; a failure lists exactly which ROMs failed or produced no signature.
/// </summary>
internal sealed class MooneyeStage : IPostStage {
    private readonly string m_group;
    private readonly string m_relativeDirectory;
    private readonly bool m_recurse;

    /// <summary>Initializes a new instance of the <see cref="MooneyeStage"/> class.</summary>
    /// <param name="group">The group name (also the stage-name suffix).</param>
    /// <param name="relativeDirectory">The directory under <c>mooneye-test-suite/acceptance/</c> (empty for the root).</param>
    /// <param name="recurse">Whether to descend into sub-directories.</param>
    public MooneyeStage(string group, string relativeDirectory, bool recurse) {
        ArgumentException.ThrowIfNullOrEmpty(argument: group);
        ArgumentNullException.ThrowIfNull(argument: relativeDirectory);

        m_group = group;
        m_recurse = recurse;
        m_relativeDirectory = relativeDirectory;
    }

    /// <inheritdoc/>
    public string Name =>
        $"mooneye-{m_group}";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = RomCatalog.Mooneye(root: context.TestRomRoot, group: m_group, relativeDirectory: m_relativeDirectory, recurse: m_recurse);

        if (cases.Count == 0) {
            return PostStageOutcome.Skip(detail: $"no mooneye ROMs under acceptance/{m_relativeDirectory} (set PUCK_GB_TESTROMS)");
        }

        var failures = new List<string>();
        var passed = 0;

        foreach (var romCase in cases) {
            try {
                var (result, detail) = MooneyeProbe.Run(romCase: romCase);

                if (result == true) {
                    ++passed;
                } else {
                    failures.Add(item: $"{romCase.Name}[{romCase.Model}] ({detail})");
                }
            } catch (Exception exception) {
                failures.Add(item: $"{romCase.Name}[{romCase.Model}] (threw {exception.GetType().Name}: {exception.Message})");
            }
        }

        return (failures.Count == 0)
            ? PostStageOutcome.Pass(detail: $"{passed}/{cases.Count} passed")
            : PostStageOutcome.Fail(detail: $"{passed}/{cases.Count} passed; failed: {string.Join(separator: ", ", values: failures)}");
    }
}
