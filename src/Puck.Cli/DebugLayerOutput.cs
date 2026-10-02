namespace Puck.Cli;

/// <summary>
/// How a verb boots a World under its backend's validation layer (<c>--debug-layers</c>) and what the World's standard
/// error then says, both the same for every verb that boots one with it (<c>puck canary</c>, <c>puck parity</c>,
/// <c>puck qualify</c>). A validation message is a
/// <c>[vulkan-debug] validation</c> line or any <c>[d3d12-debug]</c> line, a live-object report included; the Vulkan
/// loader's general and performance notices are not. A device asked for the layer says whether it is live: a Vulkan
/// instance writes <c>[vulkan] validation layer live</c>, or that it has no debug messenger, and a Direct3D 12 device
/// writes <c>[d3d12] debug layer live</c>, or that it has no info queue. A run that never says its layer is live
/// validated nothing. The one message the layer raises by design, a pipeline-library miss, never reaches standard
/// error: the Direct3D 12 debug drain leaves it out where it reads the queue, so no reader here keeps an allowlist of
/// its own.
/// </summary>
internal static class DebugLayerOutput {
    /// <summary>The World flag that creates the GPU device with its backend's validation layer.</summary>
    public const string Flag = "--debug-layers";
    /// <summary>The prefix of every Direct3D 12 debug-layer message line.</summary>
    public const string Direct3D12DebugPrefix = "[d3d12-debug] ";
    /// <summary>The prefix of the line a Direct3D 12 device writes when the layer it was asked for reports through its
    /// info queue.</summary>
    public const string Direct3D12LivePrefix = "[d3d12] debug layer live";
    /// <summary>The prefix of the line a Direct3D 12 device writes when it was asked for the layer but has no info
    /// queue.</summary>
    public const string Direct3D12NotLoadedPrefix = "[d3d12] debug layer requested but not loaded";
    /// <summary>The prefix of the line a Vulkan instance writes when the layer it was asked for reports through its
    /// debug messenger (<c>VulkanInstance.ValidationLiveLine</c>).</summary>
    public const string VulkanLivePrefix = "[vulkan] validation layer live";
    /// <summary>The prefix of the line a Vulkan instance writes when it was asked for the layer but has no debug
    /// messenger (<c>VulkanInstance.ValidationNotLiveLine</c>).</summary>
    public const string VulkanNotLivePrefix = "[vulkan] validation layer requested but not live";
    /// <summary>The prefix of every Vulkan validation message line.</summary>
    public const string VulkanValidationPrefix = "[vulkan-debug] validation ";

    /// <summary>Returns whether a standard-error line is a validation message.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> for a <c>[vulkan-debug] validation</c> or <c>[d3d12-debug]</c> line.</returns>
    public static bool IsValidationMessage(string line) => (
        line.StartsWith(comparisonType: StringComparison.Ordinal, value: VulkanValidationPrefix) ||
        line.StartsWith(comparisonType: StringComparison.Ordinal, value: Direct3D12DebugPrefix)
    );
    /// <summary>Returns whether a standard-error line says the requested layer is not live, so nothing was
    /// validated.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> for the Direct3D 12 not-loaded line or the Vulkan not-live line.</returns>
    public static bool IsBlind(string line) => (
        line.StartsWith(comparisonType: StringComparison.Ordinal, value: Direct3D12NotLoadedPrefix) ||
        line.StartsWith(comparisonType: StringComparison.Ordinal, value: VulkanNotLivePrefix)
    );
    /// <summary>Returns whether a standard-error line says the backend's requested layer is live.</summary>
    /// <param name="backend">The backend the World ran on: <c>vulkan</c> or <c>directx</c>.</param>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> for the backend's live line.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="backend"/> names no backend.</exception>
    public static bool IsLive(string backend, string line) => line.StartsWith(comparisonType: StringComparison.Ordinal, value: LivePrefix(backend: backend));
    /// <summary>Finds the first line that fails a run booted with the validation layer: a validation message, or the
    /// statement that the layer is not live.</summary>
    /// <param name="stderr">The run's standard-error lines, in order.</param>
    /// <returns>The first failing line, or <see langword="null"/> when no line fails the run.</returns>
    public static string? FirstFailure(IEnumerable<string> stderr) => stderr.FirstOrDefault(predicate: static line => (IsValidationMessage(line: line) || IsBlind(line: line)));
    /// <summary>Returns the World arguments that boot a leg under its backend's validation layer, or none.</summary>
    /// <param name="debugLayers">Whether the leg runs under the layer.</param>
    /// <returns><see cref="Flag"/>, or no argument.</returns>
    public static string[] Arguments(bool debugLayers) => (debugLayers ? [Flag] : []);
    /// <summary>The one rule for a leg booted with its backend's validation layer: any validation message, or the
    /// statement that the layer is not live, fails the leg, naming the first such line (<see cref="FirstFailure"/>), and
    /// so does a leg whose World never said its backend's layer is live (<see cref="IsLive"/>), because a layer that
    /// silently failed to load reports nothing. A leg booted without the layer has no such verdict.</summary>
    /// <param name="backend">The backend the leg's World ran on: <c>vulkan</c> or <c>directx</c>.</param>
    /// <param name="debugLayers">Whether the leg's World was booted with <see cref="Flag"/>.</param>
    /// <param name="stderr">The leg's standard-error lines, in order.</param>
    /// <returns>The verdict, or <see langword="null"/> when the leg ran without the layer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="backend"/> names no backend.</exception>
    public static DebugLayerVerdict? Verdict(string backend, bool debugLayers, IEnumerable<string> stderr) {
        var prefix = LivePrefix(backend: backend);

        if (!debugLayers) {
            return null;
        }

        var lines = stderr.ToArray();

        if (FirstFailure(stderr: lines) is { } failure) {
            return new DebugLayerVerdict(Detail: $"validation layer reported nothing (first: {failure})", Passed: false);
        }

        return (lines.Any(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix))
            ? new DebugLayerVerdict(Detail: "validation layer live and reported nothing", Passed: true)
            : new DebugLayerVerdict(Detail: $"validation layer never said it was live (no '{prefix}' line on stderr), so nothing was validated", Passed: false));
    }

    // The live line's prefix for a backend.
    private static string LivePrefix(string backend) => backend switch {
        "directx" => Direct3D12LivePrefix,
        "vulkan" => VulkanLivePrefix,
        _ => throw new ArgumentOutOfRangeException(actualValue: backend, message: $"'{backend}' is not a backend.", paramName: nameof(backend)),
    };
}
/// <summary>A leg's verdict under its backend's validation layer (<see cref="DebugLayerOutput.Verdict"/>).</summary>
/// <param name="Detail">What the layer reported: nothing, the first failing line, or that it never said it was live.</param>
/// <param name="Passed">Whether the layer was live and reported nothing.</param>
internal readonly record struct DebugLayerVerdict(string Detail, bool Passed);
