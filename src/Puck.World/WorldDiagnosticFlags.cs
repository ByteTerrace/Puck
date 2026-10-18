using Puck.Abstractions.Gpu;

namespace Puck.World;

/// <summary>
/// Parses the World's GPU diagnostic flags, which a developer passes and no document authors: <c>--debug-layers
/// [basic|full]</c>, the backend's validation level.
/// </summary>
public static class WorldDiagnosticFlags {
    /// <summary>Parses the value of <c>--debug-layers</c>: no value or <c>basic</c> is <see cref="GpuValidation.Basic"/>,
    /// <c>full</c> is <see cref="GpuValidation.Full"/>, and anything else is refused through
    /// <paramref name="error"/>.</summary>
    /// <param name="token">The flag's value, or <see langword="null"/> for a bare flag.</param>
    /// <param name="error">The sink for the refusal of a value that names no level.</param>
    /// <returns>The level, or <see cref="GpuValidation.Off"/> once the value was refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static GpuValidation ParseValidation(string? token, Action<string> error) {
        ArgumentNullException.ThrowIfNull(error);

        switch (token) {
            case null:
            case "basic":
                return GpuValidation.Basic;
            case "full":
                return GpuValidation.Full;
            default:
                error($"--debug-layers takes basic or full, not '{token}'.");

                return GpuValidation.Off;
        }
    }
}
