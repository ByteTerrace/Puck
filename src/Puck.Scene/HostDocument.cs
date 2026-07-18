using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The host section: how the run is presented, independent of WHAT it renders. It drives the native window (size),
/// the launcher (exit-after, present rate), the neutral presentation preferences (present mode, surface format), the
/// host backend (which graphics API hosts the window), and the backend feature toggles surfaced from environment
/// variables (ray query, GPU timing). Every field is OPTIONAL — an omitted field falls back to the demo's default, so
/// a document with no host section reproduces the demo's defaults (Vulkan host, 1280x800, vsync).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostDocument {
    /// <summary>The graphics backend that HOSTS the window: <c>"vulkan"</c> (default) or <c>"directx"</c>. On an OS
    /// without Direct3D 12 a <c>"directx"</c> request degrades to a Vulkan host (logged, not silent).</summary>
    public string? Backend { get; init; }
    /// <summary>The window CLIENT size as <c>[width, height]</c> in pixels; defaults to <c>[1280, 800]</c>. The
    /// world/overworld render resolution (and the camera aspect ratio) follow this size; a <c>validation</c> gate
    /// renders at a fixed resolution and ignores it.</summary>
    public IReadOnlyList<int>? Size { get; init; }
    /// <summary>The swapchain present mode: <c>"vsync"</c> (default), <c>"mailbox"</c>, <c>"immediate"</c>, or <c>"adaptive"</c> (VRR).</summary>
    public string? PresentMode { get; init; }
    /// <summary>The back-buffer surface format: <c>"r8g8b8a8"</c> (default) or <c>"b8g8r8a8"</c>.</summary>
    public string? SurfaceFormat { get; init; }
    /// <summary>Seconds before the run auto-exits; 0 runs until the window is closed. Defaults to 30.</summary>
    public int? ExitAfterSeconds { get; init; }
    /// <summary>The present-rate QUALITY TIER the window pump's display-aware pacer targets — a durable pacing option picked from a
    /// small SAFE set of named tiers (<see cref="PresentRateTiers.Names"/>: <c>sixty</c>, <c>one-twenty</c>,
    /// <c>display</c>), never a free numeric knob. <c>sixty</c>/<c>one-twenty</c> pin the present cadence at 60/120 Hz
    /// (each a whole number of engine ticks per slot); <c>display</c> uses an explicitly advertised VRR interval when
    /// available and otherwise follows the physical signal rate. Presentation pacing ONLY — the fixed-step simulation
    /// is untouched, so the tier never affects determinism. Null (the default, and every document authored before this
    /// field existed) is <c>sixty</c>, so the default demo is byte-unchanged. The live mid-session path is the
    /// <c>present-rate</c> console verb; the launcher's continuous present-rate knob stays reachable programmatically
    /// (CLI flags, embedding hosts) — this enumerated policy is only the user surface.</summary>
    public string? PresentRate { get; init; }
    /// <summary>Whether the window starts borderless-fullscreen (covering the monitor). Defaults to <see langword="false"/>
    /// (a normal desktop window). Borderless-fullscreen is what lets the swapchain take an independent flip, which is the
    /// prerequisite for a variable-refresh (VRR/G-SYNC/FreeSync) display to actually follow the present cadence — a normal
    /// desktop window is composited by the DWM at a fixed rate. Pairs with <c>presentMode: "adaptive"</c> for VRR.</summary>
    public bool? Fullscreen { get; init; }
    /// <summary>Whether the ray-query path is permitted (the <c>PUCK_RAY_QUERY</c> toggle); when false the ray-query
    /// world falls back to the compute beam path. When omitted the environment/default decides.</summary>
    public bool? RayQuery { get; init; }
    /// <summary>Whether per-pass GPU timing is armed at composition (the run-doc <c>host.timing</c> field, which seeds
    /// the shared timing control). When omitted, timing stays disarmed until armed live (the demo's gpu.timing switch /
    /// Puck.World's world.timing verb).</summary>
    public bool? Timing { get; init; }
    /// <summary>The genlock election: which external rhythm source the render pacer phase-aligns to. <c>"off"</c>
    /// disables genlock; a source id (e.g. <c>"camera:0"</c>) elects exactly that source; omitted = AUTO — genlock
    /// engages only while exactly one rhythm source is registered, and disengages the moment a second appears (no
    /// arbitrary winner). Producers never elect themselves; this is host pacing policy, beside <c>presentMode</c>.</summary>
    public string? Genlock { get; init; }
    /// <summary>Host-scoped feature-switch overrides, applied at composition through the engine's
    /// <c>FeatureSwitchRegistry</c> (switch dotted-name → value, e.g. <c>{ "render.scale": "quarter" }</c>). Validated
    /// here for SHAPE only — non-empty keys and non-empty values; whether a name is a known switch and whether it
    /// accepts the given value is runtime knowledge the validator does not have, so an unknown name or a rejected
    /// value is reported at composition (attributed to this field) instead of at parse time. Omitted or empty leaves
    /// every switch at its default.</summary>
    public IReadOnlyDictionary<string, string>? Features { get; init; }

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

        // The present-rate tier is validated only when PRESENT (the nullable-optional-field pattern): the enumerated
        // policy has NO free numeric values, so an unknown name is a hard reject with the valid set spelled out
        // (mirroring OverworldNode.revealedRenderScale and the presentMode "is not one of" messages).
        if ((PresentRate is not null) && !PresentRateTiers.TryParse(name: PresentRate, tier: out _)) {
            errors.Add(path: $"{path}.presentRate", message: $"'{PresentRate}' is not a valid present-rate tier; expected one of: {PresentRateTiers.ValidNames}");
        }

        // Genlock names a dynamic source id ("off", "camera:0", "net:metronome", ...), so only its SHAPE is validated
        // here — whether the named source ever registers is a runtime condition, not a document error.
        if ((Genlock is not null) && string.IsNullOrWhiteSpace(value: Genlock)) {
            errors.Add(path: $"{path}.genlock", message: "genlock must be \"off\" or a rhythm source id (e.g. \"camera:0\"); omit the field for automatic single-source election");
        }

        // Features names a dynamic switch roster the registry owns, so only SHAPE is validated here (mirroring
        // genlock): a blank key or value is always wrong; whether the key names a REGISTERED switch and whether it
        // ACCEPTS the value is composition-time knowledge, reported there with attributed errors.
        if (Features is not null) {
            foreach (var (name, value) in Features) {
                if (string.IsNullOrWhiteSpace(value: name)) {
                    errors.Add(path: $"{path}.features", message: "a features key cannot be empty or whitespace");
                } else if (string.IsNullOrWhiteSpace(value: value)) {
                    errors.Add(path: $"{path}.features[\"{name}\"]", message: $"features.\"{name}\" cannot have an empty or whitespace value");
                }
            }
        }
    }

    private static void RequireOneOf(string path, string name, string? value, IReadOnlyList<string> allowed, ValidationErrors errors) {
        if ((value is not null) && !allowed.Contains(value: value.ToLowerInvariant())) {
            errors.Add(path: path, message: $"'{value}' is not a valid {name}; expected one of: {string.Join(separator: ", ", values: allowed)}");
        }
    }
}
