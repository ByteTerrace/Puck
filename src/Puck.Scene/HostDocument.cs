using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The host section: how the run is presented, independent of WHAT it renders. It drives the native window (size),
/// the launcher (exit-after, render rate), the neutral presentation preferences (present mode, surface format), the
/// host backend (which graphics API hosts the window), and the backend feature toggles surfaced from environment
/// variables (ray query, GPU timing). Every field is OPTIONAL — an omitted field falls back to the demo's default, so
/// a document with no host section reproduces the historic <c>--world</c> defaults (Vulkan host, 960x600, vsync).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostDocument {
    /// <summary>The graphics backend that HOSTS the window: <c>"vulkan"</c> (default) or <c>"directx"</c>. On an OS
    /// without Direct3D 12 a <c>"directx"</c> request degrades to a Vulkan host (logged, not silent).</summary>
    public string? Backend { get; init; }
    /// <summary>The window size as <c>[width, height]</c> in pixels; defaults to <c>[960, 600]</c>. The world/rt render
    /// resolution (and the camera aspect ratio) follow this size; the <c>showcase</c> graph and a <c>validation</c>
    /// gate render at a fixed resolution and ignore it.</summary>
    public IReadOnlyList<int>? Size { get; init; }
    /// <summary>The swapchain present mode: <c>"vsync"</c> (default), <c>"mailbox"</c>, <c>"immediate"</c>, or <c>"adaptive"</c> (VRR).</summary>
    public string? PresentMode { get; init; }
    /// <summary>The back-buffer surface format: <c>"r8g8b8a8"</c> (default) or <c>"b8g8r8a8"</c>.</summary>
    public string? SurfaceFormat { get; init; }
    /// <summary>Seconds before the run auto-exits; 0 runs until the window is closed. Defaults to 30.</summary>
    public int? ExitAfterSeconds { get; init; }
    /// <summary>The target render rate in Hz; 0 uncaps the framerate. Defaults to 60.</summary>
    public int? RenderRate { get; init; }
    /// <summary>Whether the ray-query path is permitted (the <c>PUCK_RAY_QUERY</c> toggle); when false the ray-query
    /// world falls back to the compute beam path. When omitted the environment/default decides.</summary>
    public bool? RayQuery { get; init; }
    /// <summary>Whether per-pass GPU timing is emitted (the <c>PUCK_TIMING</c> toggle). When omitted the
    /// environment/default decides.</summary>
    public bool? Timing { get; init; }

    internal void Validate(string path, ValidationErrors errors) {
        RequireOneOf(errors: errors, name: "backend", path: $"{path}.backend", value: Backend, allowed: ["vulkan", "directx"]);
        RequireOneOf(errors: errors, name: "presentMode", path: $"{path}.presentMode", value: PresentMode, allowed: ["vsync", "mailbox", "immediate", "adaptive"]);
        RequireOneOf(errors: errors, name: "surfaceFormat", path: $"{path}.surfaceFormat", value: SurfaceFormat, allowed: ["r8g8b8a8", "b8g8r8a8"]);

        if (Size is not null) {
            if (Size.Count != 2) {
                errors.Add(path: $"{path}.size", message: $"size must be [width, height] (2 entries), found {Size.Count}");
            } else if ((Size[0] < 1) || (Size[1] < 1) || (Size[0] > 16384) || (Size[1] > 16384)) {
                errors.Add(path: $"{path}.size", message: $"size [{Size[0]}, {Size[1]}] must have width and height in [1, 16384]");
            }
        }

        if (ExitAfterSeconds is < 0) {
            errors.Add(path: $"{path}.exitAfterSeconds", message: $"exitAfterSeconds must be >= 0 (was {ExitAfterSeconds})");
        }

        if (RenderRate is < 0) {
            errors.Add(path: $"{path}.renderRate", message: $"renderRate must be >= 0 (was {RenderRate})");
        }
    }

    private static void RequireOneOf(string path, string name, string? value, IReadOnlyList<string> allowed, ValidationErrors errors) {
        if ((value is not null) && !allowed.Contains(value.ToLowerInvariant())) {
            errors.Add(path: path, message: $"'{value}' is not a valid {name}; expected one of: {string.Join(", ", allowed)}");
        }
    }
}
