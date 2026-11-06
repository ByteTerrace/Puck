namespace Puck.World;

// The World's graphics-backend vocabulary sits below the schema and the CLI, so the document's converters, the World's
// --backend flag and every CLI verb that launches a World parse one spelling through one parser.

/// <summary>Which graphics backend a world prefers. <see cref="Auto"/> — the default — picks the OS-appropriate backend,
/// so a shared world document is portable across an OS boundary; an explicit preference the running OS cannot satisfy
/// degrades loudly (a document author preference) or hard-exits (a CLI operator assertion) rather than silently
/// mispresenting.</summary>
public enum WorldBackendPreference : byte {
    /// <summary>Pick the OS-appropriate backend at boot — Direct3D 12 on Windows 10+, Vulkan elsewhere.</summary>
    Auto,

    /// <summary>Prefer Direct3D 12.</summary>
    DirectX,

    /// <summary>Prefer Vulkan.</summary>
    Vulkan,
}
/// <summary>The one spelling of <see cref="WorldBackendPreference"/>, shared by the document's JSON converter, the World's
/// <c>--backend</c> flag, the <c>host.backendRow</c> resolver, the <c>world.host</c> read-back and the CLI's World launch
/// (<c>WorldLaunch</c>), so nothing that parses or prints a backend disagrees with another.</summary>
public static class WorldBackendTokens {
    /// <summary>The token selecting automatic backend selection.</summary>
    public const string Auto = "auto";
    /// <summary>The token selecting the Direct3D 12 backend.</summary>
    public const string DirectX = "directx";
    /// <summary>The token selecting the Vulkan backend.</summary>
    public const string Vulkan = "vulkan";

    /// <summary>Every token, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Auto, DirectX, Vulkan];

    /// <summary>Returns the token for a backend preference.</summary>
    /// <param name="backend">The backend preference.</param>
    /// <returns>The lowercase token.</returns>
    public static string Token(WorldBackendPreference backend) => backend switch {
        WorldBackendPreference.DirectX => DirectX,
        WorldBackendPreference.Vulkan => Vulkan,
        _ => Auto,
    };
    /// <summary>Parses a backend token (case-insensitive), or <see langword="null"/> when the token names none.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parsed backend, or <see langword="null"/>.</returns>
    public static WorldBackendPreference? Parse(string? token) => token?.ToLowerInvariant() switch {
        Auto => WorldBackendPreference.Auto,
        DirectX => WorldBackendPreference.DirectX,
        Vulkan => WorldBackendPreference.Vulkan,
        _ => null,
    };
    /// <summary>Names why a token is refused, as every reader of a backend token words it.</summary>
    /// <param name="token">The refused token.</param>
    /// <returns>The refusal, naming the token and every accepted spelling.</returns>
    public static string Refusal(string token) => $"'{token}' names no backend; expected {string.Join(separator: ", ", values: All)}";
}
