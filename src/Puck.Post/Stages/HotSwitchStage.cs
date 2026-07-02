namespace Puck.Post;

/// <summary>
/// Tier-D stage D3. The runtime backend hot-switch WITH LIVE CONTENT, process-isolated: launches the POST's own
/// executable as a child in <c>--probe hot-switch</c> mode, where the probe presents real frames on the preferred
/// Vulkan backend, toggles the <see cref="Puck.Launcher.BackendSwitcher"/> to the registered Direct3D 12 presenter at
/// runtime, asserts the active backend actually changed, RE-TARGETS its content (releases the Vulkan resources —
/// safe, because a deactivated backend's device survives its presenter — and rebuilds the same fill on the presenter's
/// Direct3D 12 device), keeps presenting real frames there, and proves the post-switch content by a Direct3D 12-side
/// readback. This is the full seam the POST originally found broken (2026-07-01: the swap crashed after any real
/// present, because the Vulkan presenter's Deactivate destroyed the device under the node's resources — fixed
/// 2026-07-02 by keeping the device alive for the renderer singleton's own disposal). Requires the second presenter,
/// so non-Windows skips.
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

        return PostStageOutcome.Pass(detail: $"live content presented across a runtime vulkan → directx swap in an isolated run, re-targeted and verified by readback ({result.OkLine})");
    }
}
