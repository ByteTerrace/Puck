namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-B stage: run a group of blargg reference ROMs and pass only when every one of them reports success through its
/// <c>0xA000</c> result block. Unlike the Tier-A self-tests — which prove the machine is <em>deterministic</em> — this
/// proves it is <em>correct</em> against an external oracle. The group skips (never fails) when the ROM corpus is absent,
/// so the POST still runs anywhere; a failure lists exactly which ROMs and how they reported.
/// </summary>
internal sealed class BlarggStage : IPostStage {
    private readonly string m_group;
    private readonly string m_subPath;
    private readonly ConsoleModel m_model;

    /// <summary>Initializes a new instance of the <see cref="BlarggStage"/> class.</summary>
    /// <param name="group">The group name (also the stage-name suffix).</param>
    /// <param name="subPath">The path under the corpus's <c>blargg/</c> directory holding the group's ROMs.</param>
    /// <param name="model">The console model the group's ROMs run on.</param>
    public BlarggStage(string group, string subPath, ConsoleModel model) {
        ArgumentException.ThrowIfNullOrEmpty(argument: group);
        ArgumentException.ThrowIfNullOrEmpty(argument: subPath);

        m_group = group;
        m_model = model;
        m_subPath = subPath;
    }

    /// <inheritdoc/>
    public string Name =>
        $"blargg-{m_group}";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = RomCatalog.Blargg(root: context.TestRomRoot, group: m_group, subPath: m_subPath, model: m_model);

        if (cases.Count == 0) {
            return PostStageOutcome.Skip(detail: $"no ROMs under blargg/{m_subPath} (set PUCK_GB_TESTROMS)");
        }

        var failures = new List<string>();
        var passed = 0;

        foreach (var romCase in cases) {
            try {
                var (result, detail) = BlarggProbe.Run(romCase: romCase);

                if (result == BlarggResult.Pass) {
                    ++passed;
                } else {
                    failures.Add(item: $"{romCase.Name} ({detail})");
                }
            } catch (Exception exception) {
                failures.Add(item: $"{romCase.Name} (threw {exception.GetType().Name}: {exception.Message})");
            }
        }

        return (failures.Count == 0)
            ? PostStageOutcome.Pass(detail: $"{passed}/{cases.Count} passed on {m_model}")
            : PostStageOutcome.Fail(detail: $"{passed}/{cases.Count} passed on {m_model}; failed: {string.Join(separator: ", ", values: failures)}");
    }
}
