namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Where a render-hash floor's ROM is sourced from.</summary>
internal enum RenderFloorSource {
    /// <summary>Under the reference conformance-corpus root (<c>--roms</c> / <c>PUCK_AGB_TESTROMS</c>).</summary>
    Corpus,
    /// <summary>Under the commercial-ROM directory (<c>--games</c> / <c>PUCK_AGB_GAMES</c>).</summary>
    Games,
}

/// <summary>One deterministic render-hash floor: a ROM run for a fixed number of instructions whose framebuffer must hash
/// to a known value.</summary>
/// <param name="Source">Which root the ROM lives under.</param>
/// <param name="RelativePath">The ROM path relative to that root (forward slashes).</param>
/// <param name="Name">The floor's display name.</param>
/// <param name="Steps">The number of instructions to step before hashing.</param>
/// <param name="ExpectedHash">The known-good FNV-1a framebuffer hash (captured with a real BIOS when <paramref name="NeedsBios"/>).</param>
/// <param name="NeedsBios">Whether the ROM's render depends on the BIOS (a commercial game that runs BIOS SWIs during boot);
/// such a floor skips when only the zeroed BIOS stub is present, since it would otherwise render a blank screen and
/// mismatch. Simple direct-boot demos (the ppu screens) are BIOS-independent and set this <see langword="false"/>.</param>
internal sealed record RenderFloor(RenderFloorSource Source, string RelativePath, string Name, long Steps, ulong ExpectedHash, bool NeedsBios);

/// <summary>
/// Tier-B stage: deterministic render-hash floors. Each floor boots a ROM, runs it for a fixed number of instructions,
/// and hashes the framebuffer; because the core is fully deterministic, a known-good render must reproduce its FNV-1a
/// hash exactly. This guards the whole CPU&#8594;bus&#8594;PPU pipeline against silent regressions while the accuracy
/// frontier is worked. Floors sourced from the corpus (the ppu screen demos) or the commercial-ROM directory skip
/// individually when their ROM is absent; the stage skips entirely when none is present. Re-capture a shifted floor with
/// <c>--render-hash</c> after confirming the frame is still visually correct.
/// </summary>
internal sealed class RenderHashStage : IPostStage {
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
        var failures = new List<string>();
        var passed = 0;
        var ran = 0;

        foreach (var floor in m_floors) {
            var root = ((floor.Source == RenderFloorSource.Corpus) ? context.TestRomRoot : context.GamesRoot);

            if (root is null) {
                continue;
            }

            // A BIOS-dependent floor renders a blank screen on the zeroed stub, which would never match — skip it there
            // rather than false-fail; it only reproduces its floor with a real replacement BIOS.
            if (floor.NeedsBios && (context.BiosImage.Span.IndexOfAnyExcept(value: (byte)0) < 0)) {
                continue;
            }

            var fullPath = Path.Combine(path1: root, path2: floor.RelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));

            if (!File.Exists(path: fullPath)) {
                continue;
            }

            ++ran;

            try {
                var (pass, _, detail) = RenderHashProbe.Run(romPath: fullPath, steps: floor.Steps, expected: floor.ExpectedHash, bios: context.BiosImage);

                if (pass == true) {
                    ++passed;
                } else {
                    failures.Add(item: $"{floor.Name} ({detail})");
                }
            } catch (Exception exception) {
                failures.Add(item: $"{floor.Name} (threw {exception.GetType().Name}: {exception.Message})");
            }
        }

        if (ran == 0) {
            return PostStageOutcome.Skip(detail: "no render-hash floor ROMs present (set PUCK_AGB_TESTROMS / PUCK_AGB_GAMES)");
        }

        return ((failures.Count == 0)
            ? PostStageOutcome.Pass(detail: $"{passed}/{ran} floors reproduced")
            : PostStageOutcome.Fail(detail: $"{passed}/{ran} reproduced; drifted: {string.Join(separator: ", ", values: failures)}"));
    }
}
