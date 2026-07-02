namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-B <em>measurement</em> stage: run the AGS aging cartridge (the TCHK10 dump) headlessly and report its per-cell
/// pass count. The cartridge is patched in memory so each test writes its result flags, which a tracing bus captures. One
/// cell (the multiplayer-SIO test) genuinely needs a physical link partner and cannot pass on a single console, so this
/// stage is a measurement rather than a gate — it never fails the POST. The ROM is located from the <c>PUCK_GBA_AGS</c>
/// environment variable (only the TCHK10 dump matches the patch offsets); the stage skips when it is unset or missing.
/// The same runner backs the <c>--ags</c> diagnostic (which prints the per-cell stream and timing diagnostics).
/// </summary>
internal sealed class AgsStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "ags";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var romPath = Environment.GetEnvironmentVariable(variable: "PUCK_GBA_AGS");

        if (string.IsNullOrEmpty(value: romPath) || !File.Exists(path: romPath)) {
            return PostStageOutcome.Skip(detail: "no AGS aging-cartridge ROM (set PUCK_GBA_AGS to the TCHK10 dump)");
        }

        Diagnostics.BiosImage = context.BiosImage;

        var failed = Diagnostics.RunAgs(romPath: romPath, name: Path.GetFileName(path: romPath));

        return (failed == 0)
            ? PostStageOutcome.Pass(detail: "all captured AGS cells passed (measurement — accuracy frontier, not a gate)")
            : PostStageOutcome.Pass(detail: $"{failed} AGS cell(s) failed (measurement — includes the multiplayer-SIO cell that needs a link partner; not a gate)");
    }
}
