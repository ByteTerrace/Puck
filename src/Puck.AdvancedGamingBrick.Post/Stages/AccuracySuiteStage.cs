namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-B <em>measurement</em> stage: run the menu-driven accuracy suite headlessly and report how many of its suites
/// fully pass. Unlike the strict conformance / fuzz gates, the accuracy suite is a broad frontier where partial
/// conformance is expected, so this stage never fails the POST — it records the score for tracking. The suite ROM is
/// located from the <c>PUCK_AGB_ACCURACY_SUITE</c> environment variable; the stage skips when it is unset or missing.
/// The same runner backs the <c>--accuracy-suite</c> diagnostic (which prints the per-suite / per-subtest detail).
/// </summary>
internal sealed class AccuracySuiteStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "accuracy-suite";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var romPath = Environment.GetEnvironmentVariable(variable: "PUCK_AGB_ACCURACY_SUITE");

        if (string.IsNullOrEmpty(value: romPath) || !File.Exists(path: romPath)) {
            return PostStageOutcome.Skip(detail: "no accuracy-suite ROM (set PUCK_AGB_ACCURACY_SUITE)");
        }

        Diagnostics.BiosImage = context.BiosImage;

        var failed = Diagnostics.RunAccuracySuite(romPath: romPath, name: "accuracy suite");
        var passed = (Diagnostics.AccuracySuiteCount - failed);

        return PostStageOutcome.Pass(detail: $"{passed}/{Diagnostics.AccuracySuiteCount} suites fully passed (measurement — accuracy frontier, not a gate)");
    }
}
