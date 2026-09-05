namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-B <em>measurement</em> stage: run the menu-driven accuracy suite headlessly and report how many of its suites
/// fully pass. Unlike the strict conformance / fuzz gates, the accuracy suite is a broad frontier where partial
/// conformance is expected, so this stage never fails the POST — it records the score for tracking. The suite ROM is
/// named by <c>--accuracy-suite</c>; the stage skips when it is absent.
/// The same runner backs the <c>--accuracy-suite</c> diagnostic (which prints the per-suite / per-subtest detail).
/// </summary>
internal sealed class AccuracySuiteStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name =>
        "accuracy-suite";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var romPath = context.AccuracySuitePath;

        if (romPath is null) {
            return PostStageOutcome.Skip(detail: "no accuracy-suite ROM (pass --accuracy-suite)");
        }

        Diagnostics.BiosImage = context.BiosImage;

        var failed = Diagnostics.RunAccuracySuite(
            name: "accuracy suite",
            romPath: romPath
        );
        var passed = (Diagnostics.AccuracySuiteCount - failed);

        return PostStageOutcome.Pass(detail: $"{passed}/{Diagnostics.AccuracySuiteCount} suites fully passed (measurement — accuracy frontier, not a gate)");
    }
}
