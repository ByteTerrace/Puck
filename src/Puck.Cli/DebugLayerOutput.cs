namespace Puck.Cli;

/// <summary>
/// How a verb boots a World under its backend's validation layer (<c>--debug-layers</c>) and what the World's standard
/// error then says, both the same for every verb that boots one with it (<c>puck canary</c>, <c>puck parity</c>,
/// <c>puck qualify</c>). A validation message is a
/// <c>[vulkan-debug] validation</c> line or any <c>[d3d12-debug]</c> line, a live-object report included; the Vulkan
/// loader's general and performance notices are not. A Direct3D 12 device that was asked for the layer and has no info
/// queue says so, and a run under it validated nothing. The one message the layer raises by design, a pipeline-library
/// miss, never reaches standard error: the Direct3D 12 debug drain leaves it out where it reads the queue, so no reader
/// here keeps an allowlist of its own.
/// </summary>
internal static class DebugLayerOutput {
    /// <summary>The World flag that creates the GPU device with its backend's validation layer.</summary>
    public const string Flag = "--debug-layers";
    /// <summary>The prefix of every Direct3D 12 debug-layer message line.</summary>
    public const string Direct3D12DebugPrefix = "[d3d12-debug] ";
    /// <summary>The prefix of the line a Direct3D 12 device writes when it was asked for the layer but has no info
    /// queue.</summary>
    public const string Direct3D12NotLoadedPrefix = "[d3d12] debug layer requested but not loaded";
    /// <summary>The prefix of every Vulkan validation message line.</summary>
    public const string VulkanValidationPrefix = "[vulkan-debug] validation ";

    /// <summary>Returns whether a standard-error line is a validation message.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> for a <c>[vulkan-debug] validation</c> or <c>[d3d12-debug]</c> line.</returns>
    public static bool IsValidationMessage(string line) => (
        line.StartsWith(comparisonType: StringComparison.Ordinal, value: VulkanValidationPrefix) ||
        line.StartsWith(comparisonType: StringComparison.Ordinal, value: Direct3D12DebugPrefix)
    );
    /// <summary>Returns whether a standard-error line says the requested layer never loaded, so nothing was
    /// validated.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> for the Direct3D 12 not-loaded line.</returns>
    public static bool IsBlind(string line) => line.StartsWith(comparisonType: StringComparison.Ordinal, value: Direct3D12NotLoadedPrefix);
    /// <summary>Finds the first line that fails a run booted with the validation layer: a validation message, or the
    /// statement that the layer never loaded.</summary>
    /// <param name="stderr">The run's standard-error lines, in order.</param>
    /// <returns>The first failing line, or <see langword="null"/> when the layer loaded and reported nothing.</returns>
    public static string? FirstFailure(IEnumerable<string> stderr) => stderr.FirstOrDefault(predicate: static line => (IsValidationMessage(line: line) || IsBlind(line: line)));
    /// <summary>Returns the World arguments that boot a leg under its backend's validation layer, or none.</summary>
    /// <param name="debugLayers">Whether the leg runs under the layer.</param>
    /// <returns><see cref="Flag"/>, or no argument.</returns>
    public static string[] Arguments(bool debugLayers) => (debugLayers ? [Flag] : []);
    /// <summary>The one rule for a leg booted with its backend's validation layer: any validation message, or the
    /// statement that the layer never loaded, fails the leg, and the verdict names the first such line
    /// (<see cref="FirstFailure"/>). A leg booted without the layer has no such verdict.</summary>
    /// <param name="debugLayers">Whether the leg's World was booted with <see cref="Flag"/>.</param>
    /// <param name="stderr">The leg's standard-error lines, in order.</param>
    /// <returns>The verdict, or <see langword="null"/> when the leg ran without the layer.</returns>
    public static DebugLayerVerdict? Verdict(bool debugLayers, IEnumerable<string> stderr) {
        if (!debugLayers) {
            return null;
        }

        return ((FirstFailure(stderr: stderr) is { } failure)
            ? new DebugLayerVerdict(Detail: $"validation layer reported nothing (first: {failure})", Passed: false)
            : new DebugLayerVerdict(Detail: "validation layer reported nothing", Passed: true));
    }
}
/// <summary>A leg's verdict under its backend's validation layer (<see cref="DebugLayerOutput.Verdict"/>).</summary>
/// <param name="Detail">What the layer reported: nothing, or the first failing line.</param>
/// <param name="Passed">Whether the layer loaded and reported nothing.</param>
internal readonly record struct DebugLayerVerdict(string Detail, bool Passed);
