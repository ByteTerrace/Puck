namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-B <em>measurement</em> stage: run the menu-driven mGBA test suite (mgba-emu/suite) headlessly and report how many
/// of its suites fully pass. Unlike the strict jsmolka / FuzzARM gates, the mGBA suite is a broad accuracy frontier where
/// partial conformance is expected, so this stage never fails the POST — it records the score for tracking. The suite ROM
/// is located from the <c>PUCK_GBA_MGBA_SUITE</c> environment variable; the stage skips when it is unset or missing. The
/// same runner backs the <c>--mgba-suite</c> diagnostic (which prints the per-suite / per-subtest detail).
/// </summary>
internal sealed class MgbaSuiteStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "mgba-suite";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var romPath = Environment.GetEnvironmentVariable(variable: "PUCK_GBA_MGBA_SUITE");

        if (string.IsNullOrEmpty(value: romPath) || !File.Exists(path: romPath)) {
            return PostStageOutcome.Skip(detail: "no mGBA test suite ROM (set PUCK_GBA_MGBA_SUITE)");
        }

        Diagnostics.BiosImage = context.BiosImage;

        var failed = Diagnostics.RunMgbaSuite(romPath: romPath, name: "mGBA suite");
        var passed = (Diagnostics.MgbaSuiteCount - failed);

        return PostStageOutcome.Pass(detail: $"{passed}/{Diagnostics.MgbaSuiteCount} suites fully passed (measurement — accuracy frontier, not a gate)");
    }
}
