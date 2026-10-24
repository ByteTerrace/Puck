namespace Puck.AdvancedGamingBrick.Post;

/// <summary>One deterministic render-hash floor: a corpus ROM run for a fixed number of CPU steps whose framebuffer must
/// hash to a known value.</summary>
/// <param name="RelativePath">The ROM path relative to the conformance-corpus root (forward slashes).</param>
/// <param name="Name">The floor's display name.</param>
/// <param name="Steps">The number of CPU steps, including halted idle steps, before hashing.</param>
/// <param name="ExpectedHash">The known-good FNV-1a framebuffer hash.</param>
internal sealed record RenderFloor(string RelativePath, string Name, long Steps, ulong ExpectedHash);
/// <summary>
/// Tier-B stage: deterministic render-hash floors. Each floor boots a corpus ROM, runs it for a fixed number of CPU
/// steps, and hashes the framebuffer; because the core is fully deterministic, a known-good render must reproduce its
/// FNV-1a hash exactly. This guards the whole CPU&#8594;bus&#8594;PPU pipeline against silent regressions while the
/// accuracy frontier is worked. A floor skips when its ROM is absent; the stage skips entirely when none is present.
/// Keep BIOS identity and step budget fixed when comparing builds: replacement firmware need not reach the same frame
/// after the same instruction count. Explain a changed result before recapturing a floor with <c>--render-hash</c>.
/// </summary>
internal sealed class RenderHashStage : IPostStage<PostContext> {
    private readonly IReadOnlyList<RenderFloor> m_floors;

    /// <summary>Initializes a new instance of the <see cref="RenderHashStage"/> class.</summary>
    /// <param name="floors">The floors to check, in order.</param>
    public RenderHashStage(IReadOnlyList<RenderFloor> floors) {
        ArgumentNullException.ThrowIfNull(argument: floors);

        m_floors = floors;
    }

    /// <inheritdoc/>
    public bool IsConcurrent =>
        true;
    /// <inheritdoc/>
    public string Name =>
        "render-hash";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var root = context.TestRomRoot;
        var floors = new List<(RenderFloor Floor, string FullPath)>(capacity: m_floors.Count);

        if (root is not null) {
            foreach (var floor in m_floors) {
                var fullPath = Path.Combine(
                    path1: root,
                    path2: floor.RelativePath
                );

                if (File.Exists(path: fullPath)) {
                    floors.Add(item: (floor, fullPath));
                }
            }
        }

        if (floors.Count == 0) {
            return PostStageOutcome.Skip(detail: "no render-hash floor ROMs present (fetch the gba-tests corpus or pass --roms)");
        }

        var outcome = RomCaseRunner.Run(
            cases: floors
                .Select(selector: static item => new RomCase(
                FullPath: item.FullPath,
                Group: "render-hash",
                Name: item.Floor.Name
            ))
                .ToArray(),
            parallelism: context.Parallelism,
            probe: romCase => {
                var floor = floors[floors.FindIndex(match: item => (item.Floor.Name == romCase.Name))].Floor;

                var (pass, _, detail) = RenderHashProbe.Run(
                    bios: context.BiosImage,
                    expected: floor.ExpectedHash,
                    romPath: romCase.FullPath,
                    steps: floor.Steps
                );

                return ((pass == true), detail);
            }
        );

        return outcome;
    }
}
