using System.Runtime.InteropServices;

namespace Puck.Bench;

/// <summary>
/// The neutral, self-describing facts a <c>puck.bench.v1</c> report stamps into its <c>engine</c>/<c>host</c> blocks
/// so results remain attributable and comparable without coupling the harness to a backend or GPU API.
/// <see cref="Detect"/> supplies process-level facts and initializes host-specific fields to <see cref="Unknown"/>;
/// the composition root supplies those fields through <see cref="BenchRuntime.AttachHostInfo"/>.
/// </summary>
/// <param name="Os">The OS description (<see cref="RuntimeInformation.OSDescription"/>).</param>
/// <param name="ProcessorCount">The logical processor count (<see cref="Environment.ProcessorCount"/>).</param>
/// <param name="GpuName">The active GPU's reported name, or <see cref="Unknown"/>.</param>
/// <param name="Backend">The active render backend (<c>vulkan</c>/<c>d3d12</c>), or <see cref="Unknown"/>.</param>
/// <param name="ResolutionWidth">The swapchain width in pixels, or 0 when unknown.</param>
/// <param name="ResolutionHeight">The swapchain height in pixels, or 0 when unknown.</param>
/// <param name="PresentMode">The swapchain present mode (<c>immediate</c>/<c>mailbox</c>/<c>fifo</c>...), or
/// <see cref="Unknown"/>.</param>
/// <param name="PresentRateTier">The live <c>present.rate</c> switch tier at attach time, or <see cref="Unknown"/>.</param>
/// <param name="RenderScaleTier">The live <c>render.scale</c> switch tier at attach time, or <see cref="Unknown"/>.</param>
/// <param name="GitCommit">The short commit hash the host was built from, or <see cref="Unknown"/>.</param>
/// <param name="GitBranch">The branch the host was built from, or <see cref="Unknown"/>.</param>
/// <param name="Configuration">The build configuration (<c>Debug</c>/<c>Release</c>), detected via conditional
/// compilation.</param>
public sealed record BenchHostInfo(
    string Os,
    int ProcessorCount,
    string GpuName,
    string Backend,
    int ResolutionWidth,
    int ResolutionHeight,
    string PresentMode,
    string PresentRateTier,
    string RenderScaleTier,
    string GitCommit,
    string GitBranch,
    string Configuration
) {
    /// <summary>The sentinel value every host-known field defaults to before a composition root attaches real
    /// values.</summary>
    public const string Unknown = "unknown";

    /// <summary>Builds the process-known baseline: OS, processor count, and build configuration are real; every
    /// field only the host/composition root can know defaults to <see cref="Unknown"/>.</summary>
    /// <returns>The detected baseline host info.</returns>
    public static BenchHostInfo Detect() =>
        new(
            Backend: Unknown,
            Configuration: DetectConfiguration(),
            GitBranch: Unknown,
            GitCommit: Unknown,
            GpuName: Unknown,
            Os: RuntimeInformation.OSDescription,
            PresentMode: Unknown,
            PresentRateTier: Unknown,
            ProcessorCount: Environment.ProcessorCount,
            RenderScaleTier: Unknown,
            ResolutionHeight: 0,
            ResolutionWidth: 0
        );

    private static string DetectConfiguration() {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
}
