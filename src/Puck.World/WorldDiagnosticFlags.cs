using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;

namespace Puck.World;

/// <summary>
/// Parses the World's GPU diagnostic flags, which a developer passes and no document authors: <c>--debug-layers
/// [basic|full]</c>, the backend's validation level.
/// </summary>
public static class WorldDiagnosticFlags {
    /// <summary>Parses the value of <c>--debug-layers</c>: no value is <see cref="GpuValidation.Basic"/>, a level's wire
    /// name (<c>off</c>, <c>basic</c>, <c>full</c>, <see cref="EnumWireName{TEnum}"/>) is that level, and anything else
    /// is refused through <paramref name="error"/>.</summary>
    /// <param name="token">The flag's value, or <see langword="null"/> for a bare flag.</param>
    /// <param name="error">The sink for the refusal of a value that names no level.</param>
    /// <returns>The level, or <see cref="GpuValidation.Off"/> once the value was refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static GpuValidation ParseValidation(string? token, Action<string> error) {
        ArgumentNullException.ThrowIfNull(error);

        if (token is null) { return GpuValidation.Basic; }
        if (EnumWireName<GpuValidation>.TryParse(name: token, value: out var level)) { return level; }
        error($"--debug-layers takes basic or full, not '{token}'.");

        return GpuValidation.Off;
    }
}
