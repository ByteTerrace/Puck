using Puck.Abstractions.Presentation;

namespace Puck.World;

/// <summary>
/// The world's effective host-section values after the CLI window/backend flags override the world-doc defaults — the
/// direct twin of <see cref="WorldStorageSettings"/> for the presentation host-section. Resolved once at boot by
/// <see cref="Resolve"/> (a pure static) and registered as a singleton the <c>Program</c> registrations and the
/// <c>world.host</c> read verb consume. Backend resolution is the one place authority differs by source: a CLI
/// <c>--backend directx</c> the OS cannot satisfy is an operator assertion (<see cref="BackendUnsatisfiable"/> → the
/// caller hard-exits, preserving World's current behavior), while a document <c>backend</c> preference the OS cannot
/// satisfy is an author preference (<see cref="BackendDowngraded"/> → hosts on Vulkan with a loud line), because a
/// shared world file must never brick on someone else's machine.
/// </summary>
/// <param name="Presentation">The effective boot shape — <see cref="WorldHostPresentation.Windowed"/> composes the GPU
/// host and render root, <see cref="WorldHostPresentation.None"/> composes the authoritative core alone. See
/// <see cref="Headless"/>.</param>
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
/// <param name="Genlock">The external-clock election policy (shape-only validation; the registry interprets the id), or <see langword="null"/> for automatic election.</param>
/// <param name="Listen">The effective TCP listen endpoint (<c>host:port</c>), or <see langword="null"/> to stay
/// loopback-only.</param>
internal sealed record WorldHostSettings(
    WorldHostPresentation Presentation,
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
    string? Genlock,
    string? Listen
) {
    /// <summary>Whether this boot composes the authoritative core alone — no window, no GPU device, no swapchain, no
    /// audio device. The single predicate <c>Program.cs</c> branches boot-shape registration on.</summary>
    public bool Headless => (Presentation == WorldHostPresentation.None);
    /// <summary>The launcher present target: the boot Hz, or <see langword="null"/> for automatic display pacing (the
    /// <c>0</c>-means-automatic convention <see cref="Puck.Launcher.PresentPacingControl"/> uses).</summary>
    public double? TargetRenderRate => ((TargetHertz > 0.0)
        ? TargetHertz
        : null
    );

    /// <summary>Resolves the effective host settings by overlaying the CLI window/backend flags over the world-doc host
    /// defaults (an absent flag keeps the authored default). Stays PURE: it returns the degraded backend plus the
    /// <see cref="BackendUnsatisfiable"/> / <see cref="BackendDowngraded"/> flags, and the caller decides whether to
    /// exit (a CLI assertion) or continue (a document preference).</summary>
    /// <param name="defaults">The world-doc host defaults (absence already coalesced to <see cref="WorldHostDefaults.Absent"/> — no presentation).</param>
    /// <param name="directXAvailable">Whether the Direct3D 12 backend is available on this OS.</param>
    /// <param name="backendOverride">The parsed <c>--backend</c> value, or <see langword="null"/> to let the document decide.</param>
    /// <param name="widthOverride">The <c>--width</c> value, or <see langword="null"/>.</param>
    /// <param name="heightOverride">The <c>--height</c> value, or <see langword="null"/>.</param>
    /// <param name="exitAfterSecondsOverride">The <c>--exit-after-seconds</c> value, or <see langword="null"/>.</param>
    /// <param name="presentModeOverride">The parsed <c>--present-mode</c> value, or <see langword="null"/>.</param>
    /// <param name="presentationOverride">The parsed <c>--headless</c> reflection (<see cref="WorldHostPresentation.None"/>
    /// for a bare/true flag, <see cref="WorldHostPresentation.Windowed"/> for an explicit <c>--headless false</c>), or
    /// <see langword="null"/> to let the document's <see cref="WorldHostDefaults.Presentation"/> decide.</param>
    /// <param name="listenOverride">The <c>--listen</c> value, or <see langword="null"/> to let the document's
    /// <see cref="WorldHostDefaults.Listen"/> decide.</param>
    /// <returns>The effective host settings.</returns>
    public static WorldHostSettings Resolve(
        WorldHostDefaults defaults,
        bool directXAvailable,
        WorldBackendPreference? backendOverride,
        int? widthOverride,
        int? heightOverride,
        int? exitAfterSecondsOverride,
        PresentMode? presentModeOverride,
        WorldHostPresentation? presentationOverride = null,
        string? listenOverride = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        // The document's own backend is settled by WorldDrawBootResolver before anything reaches here (a drawn
        // backendDraw becomes an ordinary literal), so a null at this point means the document authored neither —
        // which reads as Auto, exactly as it did when the field was non-nullable.
        var requested = (backendOverride ?? (defaults.Backend ?? WorldBackendPreference.Auto));
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
        if (
            wantsDirectX &&
            !directXAvailable
        ) {
            wantsDirectX = false;

            if (fromCli) {
                unsatisfiable = true;
            } else {
                downgraded = true;
            }
        }

        return new WorldHostSettings(
            Presentation: (presentationOverride ?? defaults.Presentation),
            HostsOnDirectX: wantsDirectX,
            RequestedBackend: requested,
            BackendFromCli: fromCli,
            BackendUnsatisfiable: unsatisfiable,
            BackendDowngraded: downgraded,
            Width: Math.Max(
                val1: 1,
                val2: (widthOverride ?? defaults.Width)
            ),
            Height: Math.Max(
                val1: 1,
                val2: (heightOverride ?? defaults.Height)
            ),
            SurfaceFormat: defaults.SurfaceFormat,
            Fullscreen: defaults.Fullscreen,
            PresentMode: (presentModeOverride ?? defaults.PresentMode),
            TargetHertz: defaults.TargetHertz,
            ExitAfterSeconds: (exitAfterSecondsOverride ?? defaults.ExitAfterSeconds),
            RayQuery: defaults.RayQuery,
            Timing: defaults.Timing,
            Genlock: defaults.Genlock,
            Listen: (listenOverride ?? defaults.Listen)
        );
    }
}
