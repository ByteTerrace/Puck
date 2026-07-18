namespace Puck.Post;

/// <summary>
/// Tier-B native capture lifetime contract. The hostile target lives in a child process so a regression in the
/// platform feed cannot wedge the main battery; the child exercises pixels, fixed dimensions, resize/minimize/close,
/// concurrent consumption, a permanently non-pumping target, repeated resource reclamation, and a lenient
/// whole-monitor feed.
/// </summary>
internal sealed class CaptureStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "capture";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) {
            return PostStageOutcome.Skip(detail: "window capture requires Windows 10 2004 (build 19041) or newer");
        }

        var result = PostProbeProcess.RunCaptureLifetime();
        if (result.TimedOut) {
            return PostStageOutcome.Fail(detail: $"the hostile-window capture probe hung and was killed after {PostProbeProcess.TimeoutSeconds}s ({result.OutputTail})");
        }

        if ((0 == result.ExitCode) && result.Output.Contains(value: "PROBE capture-lifetime ok", comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Pass(detail: $"compositor capture pixels and bounded lifetime verified in an isolated hostile-window run ({result.OkLine})");
        }

        if ((0 == result.ExitCode) && result.Output.Contains(value: "PROBE capture-lifetime skip", comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Skip(detail: $"the interactive Windows capture service is unavailable ({result.OutputTail})");
        }

        return PostStageOutcome.Fail(detail: $"the capture-lifetime probe exited {result.ExitCode} ({result.OutputTail})");
    }
}
