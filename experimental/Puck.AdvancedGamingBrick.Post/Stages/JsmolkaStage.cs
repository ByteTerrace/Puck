namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-B stage: run a named group of jsmolka gba-tests ROMs and pass only when every one reports success through its
/// <c>r12</c> verdict register. These suites (CPU arm/thumb, memory timing, the save-backup state machines, the nes
/// exerciser) are the primary external correctness oracle for the CPU, bus, and cartridge. The group skips (never fails)
/// when the ROM corpus is absent, so the POST still runs anywhere; a failure lists exactly which ROMs failed and how.
/// </summary>
internal sealed class JsmolkaStage : IPostStage {
    private readonly string m_group;
    private readonly IReadOnlyList<(string RelativePath, string Name)> m_cases;

    /// <summary>Initializes a new instance of the <see cref="JsmolkaStage"/> class.</summary>
    /// <param name="group">The group name (also the stage-name suffix).</param>
    /// <param name="cases">The (corpus-relative path, display name) pairs the group runs.</param>
    public JsmolkaStage(string group, IReadOnlyList<(string RelativePath, string Name)> cases) {
        ArgumentException.ThrowIfNullOrEmpty(argument: group);
        ArgumentNullException.ThrowIfNull(argument: cases);

        m_group = group;
        m_cases = cases;
    }

    /// <inheritdoc/>
    public string Name =>
        $"jsmolka-{m_group}";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = RomCatalog.Resolve(root: context.TestRomRoot, group: m_group, cases: m_cases);

        if (cases.Count == 0) {
            return PostStageOutcome.Skip(detail: $"no jsmolka {m_group} ROMs (set PUCK_GBA_TESTROMS)");
        }

        var failures = new List<string>();
        var passed = 0;

        foreach (var romCase in cases) {
            try {
                var (pass, detail) = JsmolkaProbe.Run(romCase: romCase, bios: context.BiosImage);

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
