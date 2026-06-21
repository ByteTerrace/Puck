using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.Launcher;
using Puck.Scene;

namespace Puck.Demo;

/// <summary>
/// The resolved host configuration — window size, presentation, launcher, and host backend — applied to the
/// composition root. It is the single place the historic 960x600 / vsync / 60Hz defaults live, resolved from the run
/// document's host section (<see cref="FromDocument"/>, each field falling back to the corresponding CLI flag when the
/// document omits it). Both entry paths reach this through a document now: a real <c>--run</c> document, or one the
/// legacy flags synthesize. Keeping the resolution + DI wiring here (rather than in the entry point) keeps the
/// composition root's coupling bounded.
/// </summary>
internal sealed class HostSettings {
    private const uint DefaultHeight = 600;
    private const uint DefaultRenderRate = 60;
    private const uint DefaultWidth = 960;

    /// <summary>The window width in pixels.</summary>
    public required uint Width { get; init; }
    /// <summary>The window height in pixels.</summary>
    public required uint Height { get; init; }
    /// <summary>The raw present-mode token (vsync/mailbox/immediate).</summary>
    public required string PresentMode { get; init; }
    /// <summary>The raw surface-format token (r8g8b8a8/b8g8r8a8).</summary>
    public required string SurfaceFormat { get; init; }
    /// <summary>The auto-exit duration, or <see langword="null"/> to run until the window closes.</summary>
    public required TimeSpan? ExitAfter { get; init; }
    /// <summary>The target render rate in Hz, or <see langword="null"/> to uncap.</summary>
    public required uint? RenderRate { get; init; }
    /// <summary>Whether the document/flags request a Direct3D 12 host (subject to OS availability).</summary>
    public required bool HostBackendIsDirectX { get; init; }
    /// <summary>The ray-query toggle to push to <c>PUCK_RAY_QUERY</c>, or <see langword="null"/> to leave the env/default.</summary>
    public required bool? RayQuery { get; init; }
    /// <summary>The timing toggle to push to <c>PUCK_TIMING</c>, or <see langword="null"/> to leave the env/default.</summary>
    public required bool? Timing { get; init; }

    /// <summary>Resolves the host config from a run document's host section; each omitted field falls back to the
    /// corresponding CLI flag (or the built-in default where there is no flag).</summary>
    /// <param name="host">The document's host section, or <see langword="null"/>.</param>
    /// <param name="flagBackend">The <c>--backend</c> fallback.</param>
    /// <param name="flagPresentMode">The <c>--present-mode</c> fallback.</param>
    /// <param name="flagSurfaceFormat">The <c>--surface-format</c> fallback.</param>
    /// <param name="flagExitAfterSeconds">The <c>--exit-after-seconds</c> fallback.</param>
    /// <returns>The resolved settings.</returns>
    public static HostSettings FromDocument(HostDocument? host, string flagBackend, string flagPresentMode, string flagSurfaceFormat, int flagExitAfterSeconds) {
        var size = host?.Size;
        var hasSize = (size is { Count: 2 });
        var renderRate = host?.RenderRate;

        return new HostSettings {
            ExitAfter = ToExitAfter(seconds: (host?.ExitAfterSeconds ?? flagExitAfterSeconds)),
            Height = (hasSize ? (uint)size![1] : DefaultHeight),
            HostBackendIsDirectX = IsDirectX(backend: (host?.Backend ?? flagBackend)),
            PresentMode = (host?.PresentMode ?? flagPresentMode),
            RayQuery = host?.RayQuery,
            RenderRate = ((renderRate is null) ? DefaultRenderRate : ((renderRate <= 0) ? null : (uint)renderRate)),
            SurfaceFormat = (host?.SurfaceFormat ?? flagSurfaceFormat),
            Timing = host?.Timing,
            Width = (hasSize ? (uint)size![0] : DefaultWidth),
        };
    }

    /// <summary>Applies the window, launcher, presentation, and feature-toggle settings to the service collection.</summary>
    /// <param name="services">The service collection.</param>
    public void Apply(IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        var height = Height;
        var width = Width;

        services.Configure<NativeWindowOptions>(configureOptions: options => {
            options.Height = height;
            options.Mode = NativeWindowMode.PlatformWindow;
            options.Title = "Puck: Demo";
            options.Width = width;
        });
        services.AddSingleton(implementationInstance: new LauncherOptions {
            ExitAfter = ExitAfter,
            TargetRenderRate = RenderRate,
        });
        _ = services.AddDemoPresentation(presentMode: PresentMode, surfaceFormat: SurfaceFormat);

        // Surface the env-var feature toggles as document fields: set the variable the nodes already read so the
        // existing gate logic is reused unchanged. A null field leaves the ambient environment/default in place.
        if (RayQuery is bool rayQuery) {
            Environment.SetEnvironmentVariable(variable: "PUCK_RAY_QUERY", value: (rayQuery ? "1" : "0"));
        }

        if (Timing is bool timing) {
            Environment.SetEnvironmentVariable(variable: "PUCK_TIMING", value: (timing ? "1" : "0"));
        }
    }

    /// <summary>Whether the window should host on Direct3D 12, logging (not silencing) a downgrade when a directx host
    /// was requested on an OS without Direct3D 12 support.</summary>
    /// <param name="directXAvailable">Whether the OS provides Direct3D 12.</param>
    /// <returns><see langword="true"/> when the window hosts on Direct3D 12.</returns>
    public bool ResolveHostsOnDirectX(bool directXAvailable) {
        if (HostBackendIsDirectX && !directXAvailable) {
            Console.Error.WriteLine(value: "[run] host.backend \"directx\" is unavailable for this run (the OS lacks Direct3D 12, or the ray-query world fell back to the Vulkan beam); hosting on Vulkan instead.");

            return false;
        }

        return HostBackendIsDirectX;
    }

    private static bool IsDirectX(string backend) {
        return string.Equals(backend, "directx", StringComparison.OrdinalIgnoreCase);
    }
    private static TimeSpan? ToExitAfter(int seconds) {
        return ((seconds > 0) ? TimeSpan.FromSeconds(value: seconds) : null);
    }
}
