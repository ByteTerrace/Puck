using Puck.Abstractions.Gpu;

namespace Puck.World;

/// <summary>
/// The explicit surface-format token map the host section speaks in, the one spelling shared by the document's JSON
/// converter (<c>SurfaceFormatJsonConverter</c>, in <see cref="WorldDefinitionSerialization"/>) and the <c>world.host</c>
/// read-back, so nothing that parses or prints one disagrees with the document. The generic camelCase policy would
/// spell <see cref="GpuPixelFormat.R8G8B8A8Unorm"/> <c>r8G8B8A8Unorm</c>, so it gets an explicit name here instead. The
/// backend tokens live below the schema, in <see cref="WorldBackendTokens"/>, so the CLI's World launch shares them.
/// </summary>
public static class WorldHostTokens {
    /// <summary>The document/verb token for the B8G8R8A8 surface format.</summary>
    public const string SurfaceFormatBgra = "b8g8r8a8";
    /// <summary>The document/verb token for the R8G8B8A8 surface format.</summary>
    public const string SurfaceFormatRgba = "r8g8b8a8";

    /// <summary>Parses a surface-format token (case-insensitive), or <see langword="null"/> when the token names no
    /// authorable format.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed surface format, or <see langword="null"/>.</returns>
    public static GpuPixelFormat? ParseSurfaceFormat(string? token) => token?.ToLowerInvariant() switch {
        SurfaceFormatRgba => GpuPixelFormat.R8G8B8A8Unorm,
        SurfaceFormatBgra => GpuPixelFormat.B8G8R8A8Unorm,
        _ => null,
    };
    /// <summary>Returns the document/verb token for a surface format (only the two authorable values have one).</summary>
    /// <param name="format">The surface format.</param>
    /// <returns>The lowercase token, or the enum name for a non-authorable value.</returns>
    public static string SurfaceFormatToken(GpuPixelFormat format) => format switch {
        GpuPixelFormat.R8G8B8A8Unorm => SurfaceFormatRgba,
        GpuPixelFormat.B8G8R8A8Unorm => SurfaceFormatBgra,
        _ => format.ToString(),
    };
}
