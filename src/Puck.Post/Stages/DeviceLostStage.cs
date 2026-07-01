namespace Puck.Post;

/// <summary>
/// Tier-D stage D2. Device-lost recovery, end to end and process-isolated: launches the POST's own executable as a
/// child in <c>--probe device-loss</c> mode with <c>PUCK_TEST_DEVICE_LOSS=1</c> set, so the launcher's frame loop
/// injects ONE synthetic <see cref="Puck.Abstractions.Gpu.DeviceLostException"/> at ~1 s on a healthy GPU and must catch
/// it, recover the device + presentation resources in place, and resume driving the probe node. The probe asserts it
/// survives well past the injection with its tick accumulator strictly monotonic (the recovery may not reset the
/// loop's clock); a failed recovery rethrows in the child and the non-zero exit is reported here. Runs LAST (with D3)
/// because it deliberately destabilizes a device — in its own process, per the plan's Tier-D isolation decision.
/// </summary>
internal sealed class DeviceLostStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "device-lost";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.D;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var result = PostProbeProcess.Run(
            environment: new Dictionary<string, string> { ["PUCK_TEST_DEVICE_LOSS"] = "1" },
            probe: PostProbeNode.DeviceLossProbe
        );

        if (result.TimedOut) {
            return PostStageOutcome.Fail(detail: $"the device-loss probe hung and was killed after {PostProbeProcess.TimeoutSeconds}s — recovery deadlocked ({result.OutputTail})");
        }

        if ((0 != result.ExitCode) || !result.Output.Contains(value: "PROBE device-loss ok", comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Fail(detail: $"the device-loss probe exited {result.ExitCode} — the synthetic loss was not recovered ({result.OutputTail})");
        }

        return PostStageOutcome.Pass(detail: $"a synthetic device loss was injected, caught, and recovered in-place in an isolated run ({result.OkLine})");
    }
}
