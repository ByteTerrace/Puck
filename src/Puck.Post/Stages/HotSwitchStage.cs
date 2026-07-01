namespace Puck.Post;

/// <summary>
/// Tier-D stage D3. The runtime backend hot-switch, process-isolated: launches the POST's own executable as a child
/// in <c>--probe hot-switch</c> mode, where the probe runs on the preferred Vulkan backend, toggles the
/// <see cref="Puck.Launcher.BackendSwitcher"/> to the registered Direct3D 12 presenter at runtime, asserts the
/// active backend actually changed, and drives further presented frames on the new backend before exiting. What this
/// proves is the switcher's presenter LIFECYCLE on a live window; the probe presents no content because the POST
/// isolated two engine gaps — the swap crashes after any real present, and no content-re-target seam exists across a
/// switch (see PostProbeNode's doc) — both tracked as follow-up engine work. Requires the second presenter, so
/// non-Windows skips.
/// </summary>
internal sealed class HotSwitchStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "hot-switch";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.D;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the hot-switch probe needs the Direct3D 12 presenter, which requires Windows 10.0.10240+");
        }

        var result = PostProbeProcess.Run(environment: null, probe: PostProbeNode.HotSwitchProbe);

        if (result.TimedOut) {
            return PostStageOutcome.Fail(detail: $"the hot-switch probe hung and was killed after {PostProbeProcess.TimeoutSeconds}s ({result.OutputTail})");
        }

        if ((0 != result.ExitCode) || !result.Output.Contains(value: "PROBE hot-switch ok", comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Fail(detail: $"the hot-switch probe exited {result.ExitCode} — the runtime backend switch did not complete ({result.OutputTail})");
        }

        return PostStageOutcome.Pass(detail: $"the presenter lifecycle survived a live vulkan → directx swap in an isolated run ({result.OkLine})");
    }
}
