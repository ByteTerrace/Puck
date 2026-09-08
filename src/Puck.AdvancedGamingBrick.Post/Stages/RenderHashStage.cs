namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Where a render-hash floor's ROM is sourced from.</summary>
internal enum RenderFloorSource {
    /// <summary>Under the reference conformance-corpus root (the gba-tests corpus, or <c>--roms</c>).</summary>
    Corpus,
    /// <summary>Under the commercial-ROM directory (<c>--games</c>).</summary>
    Games,
}
/// <summary>One deterministic render-hash floor: a ROM run for a fixed number of CPU steps whose framebuffer must hash
/// to a known value.</summary>
/// <param name="Source">Which root the ROM lives under.</param>
/// <param name="RelativePath">The ROM path relative to that root (forward slashes).</param>
/// <param name="Name">The floor's display name.</param>
/// <param name="Steps">The number of CPU steps, including halted idle steps, before hashing.</param>
/// <param name="ExpectedHash">The known-good FNV-1a framebuffer hash (captured with a real BIOS when <paramref name="NeedsBios"/>).</param>
/// <param name="NeedsBios">Whether the ROM's render depends on the BIOS (a commercial game that runs BIOS SWIs during boot);
/// such a floor skips when only the zeroed BIOS stub is present, since it would otherwise render a blank screen and
/// mismatch. Simple direct-boot demos (the ppu screens) are BIOS-independent and set this <see langword="false"/>.</param>
internal sealed record RenderFloor(RenderFloorSource Source, string RelativePath, string Name, long Steps, ulong ExpectedHash, bool NeedsBios);
/// <summary>
/// Tier-B stage: deterministic render-hash floors. Each floor boots a ROM, runs it for a fixed number of CPU steps,
/// and hashes the framebuffer; because the core is fully deterministic, a known-good render must reproduce its FNV-1a
/// hash exactly. This guards the whole CPU&#8594;bus&#8594;PPU pipeline against silent regressions while the accuracy
/// frontier is worked. Floors sourced from the corpus (the ppu screen demos) or the commercial-ROM directory skip
/// individually when their ROM is absent; the stage skips entirely when none is present. Re-capture a shifted floor with
/// <c>--render-hash</c> after confirming the frame is still visually correct.
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
    public string Name =>
        "render-hash";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var stubBios = (context.BiosImage.Span.IndexOfAnyExcept(value: ((byte)0)) < 0);
        var floors = new List<(RenderFloor Floor, string FullPath)>(capacity: m_floors.Count);

        foreach (var floor in m_floors) {
            var root = ((floor.Source == RenderFloorSource.Corpus)
                ? context.TestRomRoot
                : context.GamesRoot);

            // A BIOS-dependent floor renders a blank screen on the zeroed stub, which would never match — skip it there
            // rather than false-fail; it only reproduces its floor with a real replacement BIOS.
            if (
                (root is null) ||
                (floor.NeedsBios && stubBios)
            ) {
                continue;
            }

            var fullPath = Path.Combine(
                path1: root,
                path2: floor.RelativePath.Replace(
                    newChar: Path.DirectorySeparatorChar,
                    oldChar: '/'
                )
            );

            if (File.Exists(path: fullPath)) {
                floors.Add(item: (floor, fullPath));
            }
        }

        if (floors.Count == 0) {
            return PostStageOutcome.Skip(detail: "no render-hash floor ROMs present (fetch the gba-tests corpus or pass --roms / --games)");
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
