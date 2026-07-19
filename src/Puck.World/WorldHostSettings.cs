using Puck.Abstractions.Presentation;

namespace Puck.World;

/// <summary>
/// The explicit token maps the host section speaks in — the ONE spelling shared by the JSON converters
/// (<see cref="WorldBackendPreferenceJsonConverter"/>, <see cref="SurfaceFormatJsonConverter"/>) and the
/// <c>world.host.tune</c> value grammar, so the document and the verb never disagree. The two enum families that would
/// serialize badly under the generic camelCase policy (<see cref="WorldBackendPreference.DirectX"/> → <c>directX</c>;
/// <see cref="SurfaceFormat.R8G8B8A8Unorm"/> → <c>r8G8B8A8Unorm</c>) get an explicit name here instead.
/// </summary>
internal static class WorldHostTokens {
    public const string BackendAuto = "auto";
    public const string BackendDirectX = "directx";
    public const string BackendVulkan = "vulkan";
    public const string SurfaceFormatRgba = "r8g8b8a8";
    public const string SurfaceFormatBgra = "b8g8r8a8";

    /// <summary>The document/verb token for a backend preference.</summary>
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

    /// <summary>The document/verb token for a surface format (only the two authorable values have one).</summary>
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

/// <summary>
/// The world's EFFECTIVE host-section values after the CLI window/backend flags override the world-doc defaults — the
/// direct twin of <see cref="WorldStorageSettings"/> for the presentation host-section. Resolved ONCE at boot by
/// <see cref="Resolve"/> (a pure static) and registered as a singleton the <c>Program</c> registrations and the
/// <c>world.host</c> read verb consume. Backend resolution is the one place authority differs by SOURCE: a CLI
/// <c>--backend directx</c> the OS cannot satisfy is an operator assertion (<see cref="BackendUnsatisfiable"/> → the
/// caller hard-exits, preserving World's current behavior), while a document <c>backend</c> preference the OS cannot
/// satisfy is an author preference (<see cref="BackendDowngraded"/> → hosts on Vulkan with a loud line), because a
/// shared world file must never brick on someone else's machine.
/// </summary>
/// <param name="HostsOnDirectX">Whether the resolved backend is Direct3D 12 (else Vulkan).</param>
/// <param name="RequestedBackend">The backend the resolution started from (CLI override, else the document preference).</param>
/// <param name="BackendFromCli">Whether the backend request came from the CLI (an operator assertion) rather than the document.</param>
/// <param name="BackendUnsatisfiable">Whether a CLI backend assertion could not be satisfied on this OS (the caller hard-exits).</param>
/// <param name="BackendDowngraded">Whether a document backend preference was degraded to Vulkan on this OS (a loud line).</param>
/// <param name="Width">The effective window client width in pixels.</param>
/// <param name="Height">The effective window client height in pixels.</param>
/// <param name="SurfaceFormat">The effective swapchain surface format.</param>
/// <param name="Fullscreen">Whether the window enters borderless fullscreen when first shown.</param>
/// <param name="PresentMode">The effective swapchain presentation algorithm.</param>
/// <param name="TargetHertz">The boot present-pacing target in Hz (<c>0</c> = automatic display pacing).</param>
/// <param name="ExitAfterSeconds">The effective auto-exit seconds (<c>0</c> runs until the window is closed).</param>
/// <param name="RayQuery">Whether the SDF renderer may use the ray-query hardware path.</param>
/// <param name="Timing">Whether GPU per-pass timing boots armed.</param>
/// <param name="Genlock">The external-clock election policy (SHAPE-only), or <see langword="null"/> for automatic election.</param>
internal sealed record WorldHostSettings(
    bool HostsOnDirectX,
    WorldBackendPreference RequestedBackend,
    bool BackendFromCli,
    bool BackendUnsatisfiable,
    bool BackendDowngraded,
    int Width,
    int Height,
    SurfaceFormat SurfaceFormat,
    bool Fullscreen,
    PresentMode PresentMode,
    double TargetHertz,
    int ExitAfterSeconds,
    bool RayQuery,
    bool Timing,
    string? Genlock
) {
    /// <summary>The launcher present target: the boot Hz, or <see langword="null"/> for automatic display pacing (the
    /// <c>0</c>-means-automatic convention <see cref="Puck.Launcher.PresentPacingControl"/> uses).</summary>
    public double? TargetRenderRate => (TargetHertz > 0.0 ? TargetHertz : null);

    /// <summary>Resolves the effective host settings by overlaying the CLI window/backend flags over the world-doc host
    /// defaults (an absent flag keeps the authored default). Stays PURE: it returns the degraded backend plus the
    /// <see cref="BackendUnsatisfiable"/> / <see cref="BackendDowngraded"/> flags, and the caller decides whether to
    /// exit (a CLI assertion) or continue (a document preference).</summary>
    /// <param name="defaults">The world-doc host defaults (absence already coalesced to <see cref="WorldHostDefaults.Default"/>).</param>
    /// <param name="directXAvailable">Whether the Direct3D 12 backend is available on this OS.</param>
    /// <param name="backendOverride">The parsed <c>--backend</c> value, or <see langword="null"/> to let the document decide.</param>
    /// <param name="widthOverride">The <c>--width</c> value, or <see langword="null"/>.</param>
    /// <param name="heightOverride">The <c>--height</c> value, or <see langword="null"/>.</param>
    /// <param name="exitAfterSecondsOverride">The <c>--exit-after-seconds</c> value, or <see langword="null"/>.</param>
    /// <param name="presentModeOverride">The parsed <c>--present-mode</c> value, or <see langword="null"/>.</param>
    /// <returns>The effective host settings.</returns>
    public static WorldHostSettings Resolve(
        WorldHostDefaults defaults,
        bool directXAvailable,
        WorldBackendPreference? backendOverride,
        int? widthOverride,
        int? heightOverride,
        int? exitAfterSecondsOverride,
        PresentMode? presentModeOverride
    ) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        var requested = (backendOverride ?? defaults.Backend);
        var fromCli = (backendOverride is not null);
        var wantsDirectX = requested switch {
            WorldBackendPreference.DirectX => true,
            WorldBackendPreference.Vulkan => false,
            _ => directXAvailable,
        };
        var unsatisfiable = false;
        var downgraded = false;

        // A DirectX request the OS cannot satisfy splits by authority: a CLI assertion hard-exits (the caller reads the
        // flag), a document preference degrades LOUDLY to Vulkan. Auto never reaches here unsatisfiable — it resolved to
        // directXAvailable above.
        if (wantsDirectX && !directXAvailable) {
            wantsDirectX = false;

            if (fromCli) {
                unsatisfiable = true;
            } else {
                downgraded = true;
            }
        }

        return new WorldHostSettings(
            HostsOnDirectX: wantsDirectX,
            RequestedBackend: requested,
            BackendFromCli: fromCli,
            BackendUnsatisfiable: unsatisfiable,
            BackendDowngraded: downgraded,
            Width: Math.Max(val1: 1, val2: (widthOverride ?? defaults.Width)),
            Height: Math.Max(val1: 1, val2: (heightOverride ?? defaults.Height)),
            SurfaceFormat: defaults.SurfaceFormat,
            Fullscreen: defaults.Fullscreen,
            PresentMode: (presentModeOverride ?? defaults.PresentMode),
            TargetHertz: defaults.TargetHertz,
            ExitAfterSeconds: (exitAfterSecondsOverride ?? defaults.ExitAfterSeconds),
            RayQuery: defaults.RayQuery,
            Timing: defaults.Timing,
            Genlock: defaults.Genlock
        );
    }
}
