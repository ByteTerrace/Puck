using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Windowing;
using Puck.Demo.Configuration;
using Puck.Launcher;
using Puck.Scene;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// The resolved host configuration — window size, presentation, launcher, and host backend — applied to the
/// composition root. It is the single place the default-size / vsync / 60Hz defaults live, resolved from the run
/// document's host section (<see cref="FromDocument"/>, each field falling back to the corresponding CLI flag when the
/// document omits it). Both entry paths reach this through a document now: a real <c>--run</c> document, or one the
/// CLI options synthesize. Keeping the resolution + DI wiring here (rather than in the entry point) keeps the
/// composition root's coupling bounded.
/// </summary>
internal sealed class HostSettings {
    /// <summary>The default window CLIENT height. 1280x800 (up from the historic 960x600) gives the overworld's 2×2
    /// quad 640x400 per pane — a comfortable 2x-scale GamingBrick screen with margin — while still fitting a
    /// 125%-scaled 1080p desktop with window decorations.</summary>
    internal const uint DefaultHeight = 800;
    /// <summary>The default window CLIENT width (see <see cref="DefaultHeight"/>).</summary>
    internal const uint DefaultWidth = 1280;

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
    /// <summary>The target present rate in Hz (resolved from the document's enumerated present-rate tier), or
    /// <see langword="null"/> for automatic display pacing (the <c>display</c> tier).</summary>
    public required uint? RenderRate { get; init; }
    /// <summary>Whether the window starts borderless-fullscreen (the prerequisite for a VRR display to follow the present cadence).</summary>
    public required bool Fullscreen { get; init; }
    /// <summary>Whether the document/flags request a Direct3D 12 host (subject to OS availability).</summary>
    public required bool HostBackendIsDirectX { get; init; }
    /// <summary>The resolved <c>PUCK_RAY_QUERY</c> toggle (permit/deny the ray-query path), threaded into
    /// <see cref="SdfWorldRenderSpec.RayQuery"/> by the render builder, or <see langword="null"/> to leave the
    /// environment/default in place — this class never re-pushes the value into the process environment (env→config
    /// is the one direction; see <see cref="Apply"/>).</summary>
    public required bool? RayQuery { get; init; }
    /// <summary>The resolved <c>host.timing</c> toggle (per-pass GPU-ms timestamps), threaded into
    /// <see cref="SdfWorldRenderSpec.Timing"/> and seeded into the shared GPU-timing control at composition (see
    /// <see cref="Apply"/>). Unlike <see cref="RayQuery"/> it drives no environment fallback — GPU timing is armed
    /// live (the gpu.timing switch / the world.timing verb) or seeded from this field.</summary>
    public required bool? Timing { get; init; }
    /// <summary>The genlock election policy (<c>"off"</c>, a rhythm source id, or <see langword="null"/> for automatic
    /// single-source election) — host pacing policy, applied as the external-clock registry's configuration.</summary>
    public required string? Genlock { get; init; }

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
        // The present-rate TIER resolves to a target Hz (0 = the `display` tier = automatic). A null/omitted field is the
        // `sixty` default (byte-unchanged); an unknown name never reaches here (the validator rejects it), so an
        // unparsed value falls back to `sixty` defensively.
        _ = PresentRateTiers.TryParse(name: host?.PresentRate, tier: out var presentRateTier);
        var presentRateHertz = PresentRateTiers.TargetHertz(tier: presentRateTier);

        return new HostSettings {
            ExitAfter = ToExitAfter(seconds: (host?.ExitAfterSeconds ?? flagExitAfterSeconds)),
            Fullscreen = (host?.Fullscreen ?? false),
            Genlock = host?.Genlock,
            Height = (hasSize ? (uint)size![1] : DefaultHeight),
            HostBackendIsDirectX = IsDirectX(backend: (host?.Backend ?? flagBackend)),
            PresentMode = (host?.PresentMode ?? flagPresentMode),
            RayQuery = host?.RayQuery,
            RenderRate = ((presentRateHertz > 0U) ? presentRateHertz : null),
            SurfaceFormat = (host?.SurfaceFormat ?? flagSurfaceFormat),
            Timing = host?.Timing,
            Width = (hasSize ? (uint)size![0] : DefaultWidth),
        };
    }

    /// <summary>Applies the window, launcher, presentation, and feature-toggle settings to the application builder.</summary>
    /// <param name="builder">The application builder (its services + composed configuration; the launcher's <c>PUCK_*</c>
    /// runtime toggles bind from the latter).</param>
    public void Apply(IHostApplicationBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var launcherRuntime = DemoConfiguration.ResolveLauncherRuntime(configuration: builder.Configuration);
        var height = Height;
        var width = Width;
        var fullscreen = Fullscreen;

        services.Configure<NativeWindowOptions>(configureOptions: options => {
            options.Height = height;
            options.Mode = NativeWindowMode.PlatformWindow;
            options.StartFullscreen = fullscreen;
            options.Title = "Puck: Demo";
            options.Width = width;
        });
        services.AddSingleton(implementationInstance: new LauncherOptions {
            ExitAfter = ExitAfter,
            GenlockEnabled = launcherRuntime.GenlockEnabled,
            LogPresentTiming = launcherRuntime.LogPresentTiming,
            SyntheticDeviceLossSeconds = launcherRuntime.SyntheticDeviceLossSeconds,
            TargetRenderRate = RenderRate,
        });
        // The external-clock registry, configured with the document's genlock election (host pacing policy). Registered
        // as a concrete instance so it precedes — and wins over — the launcher's TryAdd default (auto election).
        services.AddSingleton(implementationInstance: new ExternalClockRegistry(electionPolicy: Genlock));
        _ = services.AddDemoPresentation(presentMode: PresentMode, surfaceFormat: SurfaceFormat);

        // Register the resolved settings themselves (env→config is the ONE direction; there is no config→env
        // re-push): the render-assembly call sites (GraphBuilder, OverworldRenderNode) resolve this singleton to
        // read RayQuery/Timing and thread them straight into SdfWorldRenderSpec/SdfEngineNode as options. RayQuery's
        // constructor argument falls back to the PUCK_RAY_QUERY environment read when null — so that env var keeps
        // working verbatim for anyone setting it externally, without this class pushing values back into the process
        // environment for the deep readers to pick up; Timing has no env fallback (it seeds the shared control below).
        services.AddSingleton(implementationInstance: this);

        // Seed the live GPU-timing arming control from the run document's host.timing field at COMPOSITION — the second
        // precedence tier (beneath a programmatic bench arm and the live gpu.timing/world.timing switches, above the
        // engine node's construction seed). Only an EXPLICIT host.timing claims the control here; when the field is
        // omitted the control is left free for a later live arm or the engine's own seed.
        if (Timing is { } timing) {
            _ = GpuTimingControl.Shared.TrySeed(armed: timing);
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
        return string.Equals(a: backend, b: "directx", comparisonType: StringComparison.OrdinalIgnoreCase);
    }
    private static TimeSpan? ToExitAfter(int seconds) {
        return ((seconds > 0) ? TimeSpan.FromSeconds(value: seconds) : null);
    }
}
