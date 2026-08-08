namespace Puck.Post;

/// <summary>
/// Tier-D stage D1. Present cadence — the CLOSED-LOOP present-timing feedback the host pacer phase-locks to, exercised
/// in an isolated multi-frame <c>--probe</c> child because the live present loop runs ABOVE the battery's single
/// one-shot frame. The probe presents real content and collects confirmed-present timings through
/// <see cref="Puck.Abstractions.Presentation.IPresentTimingFeedback"/>, asserting the feedback path is LIVE and its
/// timestamps are monotonic and plausibly spaced — the live plumbing A5's pure-CPU aligner sim cannot cover. It
/// deliberately does NOT assert VRR phase-lock convergence: that needs a variable-refresh panel (the dev display is
/// fixed-refresh), so a presenter/display that delivers no closed-loop feedback yields a <see cref="PostVerdict.Skip"/>,
/// never a <see cref="PostVerdict.Fail"/>.
/// </summary>
internal sealed class PresentCadenceStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "present-cadence";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.D;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var result = PostProbeProcess.Run(probe: PostProbeNode.PresentCadenceProbe, environment: null);

        if (result.TimedOut) {
            return PostStageOutcome.Fail(detail: $"the present-cadence probe hung and was killed after {PostProbeProcess.TimeoutSeconds}s ({result.OutputTail})");
        }

        if ((0 == result.ExitCode) && result.Output.Contains(value: "PROBE present-cadence ok", comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Pass(detail: $"closed-loop present-timing feedback verified live in an isolated run ({result.OkLine})");
        }

        if ((0 == result.ExitCode) && result.Output.Contains(value: "PROBE present-cadence skip", comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Skip(detail: $"no closed-loop present timing on this presenter/display ({result.OutputTail})");
        }

        return PostStageOutcome.Fail(detail: $"the present-cadence probe exited {result.ExitCode} — closed-loop present timing was not verified ({result.OutputTail})");
    }
}
