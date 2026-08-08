using Puck.Abstractions.Presentation;

namespace Puck.World;

/// <summary>
/// The explicit token maps the host section speaks in — the ONE spelling shared by the document's JSON converters
/// (<c>WorldBackendPreferenceJsonConverter</c>, <c>SurfaceFormatJsonConverter</c>, in
/// <see cref="WorldDefinitionSerialization"/>) and every Puck.World reader of the same tokens — the <c>--backend</c>
/// boot flag, the <c>host.backendDraw</c> resolver, and the <c>world.host</c> read-back — so nothing that parses or
/// prints one disagrees with the document. The two enum families that would serialize badly under the generic
/// camelCase policy (<see cref="WorldBackendPreference.DirectX"/> → <c>directX</c>;
/// <see cref="SurfaceFormat.R8G8B8A8Unorm"/> → <c>r8G8B8A8Unorm</c>) get an explicit name here instead.
/// </summary>
public static class WorldHostTokens {
    /// <summary>The document/verb token selecting automatic backend selection.</summary>
    public const string BackendAuto = "auto";
    /// <summary>The document/verb token selecting the DirectX backend.</summary>
    public const string BackendDirectX = "directx";
    /// <summary>The document/verb token selecting the Vulkan backend.</summary>
    public const string BackendVulkan = "vulkan";
    /// <summary>The document/verb token for the R8G8B8A8 surface format.</summary>
    public const string SurfaceFormatRgba = "r8g8b8a8";
    /// <summary>The document/verb token for the B8G8R8A8 surface format.</summary>
    public const string SurfaceFormatBgra = "b8g8r8a8";

    /// <summary>Returns the document/verb token for a backend preference.</summary>
    /// <param name="backend">The backend preference.</param>
    /// <returns>The lowercase token.</returns>
    public static string BackendToken(WorldBackendPreference backend) => backend switch {
        WorldBackendPreference.DirectX => BackendDirectX,
        WorldBackendPreference.Vulkan => BackendVulkan,
        _ => BackendAuto,
    };

    /// <summary>Parses a backend token (case-insensitive), or <see langword="null"/> when the token names none.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed backend, or <see langword="null"/>.</returns>
    public static WorldBackendPreference? ParseBackend(string? token) => token?.ToLowerInvariant() switch {
        BackendAuto => WorldBackendPreference.Auto,
        BackendDirectX => WorldBackendPreference.DirectX,
        BackendVulkan => WorldBackendPreference.Vulkan,
        _ => null,
    };

    /// <summary>Returns the document/verb token for a surface format (only the two authorable values have one).</summary>
    /// <param name="format">The surface format.</param>
    /// <returns>The lowercase token, or the enum name for a non-authorable value.</returns>
    public static string SurfaceFormatToken(SurfaceFormat format) => format switch {
        SurfaceFormat.R8G8B8A8Unorm => SurfaceFormatRgba,
        SurfaceFormat.B8G8R8A8Unorm => SurfaceFormatBgra,
        _ => format.ToString(),
    };

    /// <summary>Parses a surface-format token (case-insensitive), or <see langword="null"/> when the token names no
    /// authorable format (<c>unknown</c> is rejected by name, not accepted-then-validated).</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed surface format, or <see langword="null"/>.</returns>
    public static SurfaceFormat? ParseSurfaceFormat(string? token) => token?.ToLowerInvariant() switch {
        SurfaceFormatRgba => SurfaceFormat.R8G8B8A8Unorm,
        SurfaceFormatBgra => SurfaceFormat.B8G8R8A8Unorm,
        _ => null,
    };
}
