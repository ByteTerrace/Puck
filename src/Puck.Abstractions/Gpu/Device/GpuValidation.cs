namespace Puck.Abstractions.Gpu;

/// <summary>
/// How much of its backend's validation a device is created with: the World's <c>--debug-layers [basic|full]</c>. Each
/// level adds to the one before it, and every level above <see cref="Off"/> names the device's objects.
/// </summary>
public enum GpuValidation {
    /// <summary>No validation layer: the default, and what every shipped run uses.</summary>
    Off = 0,
    /// <summary>The API validation layer: the Khronos validation layer with synchronization validation and a messenger
    /// printing <c>[vulkan-debug]</c> lines on Vulkan; the debug layer, its <c>[d3d12-debug]</c> drain and teardown
    /// live-object report on Direct3D 12. A bare <c>--debug-layers</c> asks for it, and every verb that boots a World
    /// under its layer (<c>puck canary</c>, <c>puck parity</c>, <c>puck qualify</c>) uses it.</summary>
    Basic = 1,
    /// <summary><see cref="Basic"/>, plus what checks the GPU's own work and advises on cost: GPU-based validation with
    /// state tracking and synchronized command-queue validation on Direct3D 12; GPU-assisted validation and the
    /// best-practices checks, NVIDIA's included, on Vulkan. Shader instrumentation makes pipeline creation and every
    /// frame much slower, so it is a hunting level, never a gate's.</summary>
    Full = 2,
}
/// <summary>The one spelling of each <see cref="GpuValidation"/> level above <see cref="GpuValidation.Off"/>, shared by the
/// World's <c>--debug-layers</c> flag and every tool that passes it.</summary>
public static class GpuValidationSpelling {
    // Each level's one spelling, in declaration order.
    private static readonly (GpuValidation Level, string Name)[] Names = [
        (GpuValidation.Basic, "basic"),
        (GpuValidation.Full, "full"),
    ];

    /// <summary>Gets every spelling, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Names.Select(selector: static entry => entry.Name)];

    /// <summary>Returns a level's spelling.</summary>
    /// <param name="level">The level; not <see cref="GpuValidation.Off"/>, which no flag spells.</param>
    /// <returns>The spelling.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> has no spelling.</exception>
    public static string Of(GpuValidation level) {
        foreach (var (candidate, name) in Names) {
            if (candidate == level) { return name; }
        }

        throw new ArgumentOutOfRangeException(actualValue: level, message: "Only a validation level above off has a spelling.", paramName: nameof(level));
    }
    /// <summary>Reads one level's spelling.</summary>
    /// <param name="name">The spelling, such as <c>basic</c>.</param>
    /// <param name="level">The level it names.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> spells a level.</returns>
    public static bool TryParse(string? name, out GpuValidation level) {
        foreach (var (candidate, spelling) in Names) {
            if (string.Equals(a: spelling, b: name, comparisonType: StringComparison.Ordinal)) {
                level = candidate;

                return true;
            }
        }

        level = GpuValidation.Off;

        return false;
    }
}
