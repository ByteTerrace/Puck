using Puck.Hosting;
using Puck.Abstractions.Presentation;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The operation carried by the single render-scale lever.</summary>
public enum WorldRenderScaleOperation { Echo, Ceiling, Floor, Pin, Auto, Off }
/// <summary>A view's live resolution state. Its pin and controller never enter a saved document or replay.</summary>
public sealed class WorldRenderViewResolution {
    /// <summary>The one resolution policy, with history belonging to this view.</summary>
    public WorldDynamicResolution Controller { get; } = new();
    /// <summary>The session's automatic-mode override, or null for the world's default.</summary>
    public bool? Automatic { get; set; }
    /// <summary>The pinned scale, or zero for automatic choice.</summary>
    public float Pin { get; set; }
}
public sealed partial class WorldRenderSettings {
    private readonly Dictionary<string, WorldRenderViewResolution> m_renderViews = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, WorldViewQuality> m_renderQuality = new(comparer: StringComparer.Ordinal);

    private WorldDefinition? m_resolutionDefinition;
    private float m_defaultPin;
    private bool m_qualityChanged;

    /// <summary>The number of registered render views, for completion-accounting reconciliation.</summary>
    public int ResolutionViewCount => m_renderViews.Count;
    /// <summary>Whether any registered view needs adaptive timing and completion accounting.</summary>
    public bool AnyResolutionEnabled {
        get {
            if (DynamicResolution) { return true; }
            foreach (var state in m_renderViews.Values) {
                if ((state.Automatic == true) || (state.Pin > 0f)) { return true; }
            }
            return false;
        }
    }

    /// <summary>Reads a revised world's authored per-view quality. Live pins remain session state.</summary>
    public void ReadQuality(WorldDefinition definition) {
        if (ReferenceEquals(objA: m_resolutionDefinition, objB: definition)) {
            return;
        }
        var sameQuality = SameQuality(previous: m_resolutionDefinition?.Views.Quality, current: definition.Views.Quality);

        m_resolutionDefinition = definition;
        if (sameQuality) {
            return;
        }
        m_qualityChanged = false;
        m_renderQuality.Clear();
        foreach (var row in (definition.Views.Quality ?? [])) {
            m_renderQuality.Add(key: row.Name, value: row);
        }
    }
    /// <summary>Registers or returns a view's session state; subsequent reads allocate nothing.</summary>
    public WorldRenderViewResolution Resolution(string view) {
        if (!m_renderViews.TryGetValue(key: view, value: out var state)) {
            state = new WorldRenderViewResolution { Pin = (IsPlayerView(view: view) ? m_defaultPin : 0f) };
            m_renderViews.Add(key: view, value: state);
            if (state.Pin > 0f) {
                state.Pin = Math.Clamp(state.Pin, Floor(view: view), Ceiling(view: view));
            }
        }
        return state;
    }
    /// <summary>Whether a target names a known rendered or authored view.</summary>
    public bool HasView(string view) {
        if ((view == "*") || (view == WorldViewGraphs.WorldInstance) || m_renderViews.ContainsKey(key: view) || m_renderQuality.ContainsKey(key: view)) {
            return true;
        }
        if (m_resolutionDefinition is { } definition) {
            for (var index = 0; (index < definition.Cameras.Count); index++) {
                if (definition.Cameras[index].Name == view) { return true; }
            }
        }
        return false;
    }
    /// <summary>Checks a pin against the range it has while enabled, refusing its view and value by name.</summary>
    public bool CanPin(string view, float scale, out string? refusal) {
        if (view == "*") {
            foreach (var name in m_renderViews.Keys) {
                if (IsPlayerView(view: name) && !CanPin(refusal: out refusal, scale: scale, view: name)) { return false; }
            }
            foreach (var name in m_renderQuality.Keys) {
                if (IsPlayerView(view: name) && !CanPin(refusal: out refusal, scale: scale, view: name)) { return false; }
            }
        }
        var ceiling = Authored(view: view);

        if (RenderGraphExtent.Quantize(fraction: ceiling) >= 1d) {
            ceiling = WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.ThreeQuarter);
        }
        if (!float.IsFinite(f: scale) || (scale < Math.Min(val1: Floor(view: view), val2: ceiling)) || (scale > ceiling)) {
            refusal = $"world.render-scale: pin '{scale}' for '{view}' is outside floor {Math.Min(val1: Floor(view: view), val2: ceiling)}..ceiling {ceiling}";
            return false;
        }
        refusal = null;
        return true;
    }
    /// <summary>The view's effective allocation ceiling, retaining the native reconstruction rule.</summary>
    public float Ceiling(string view) {
        var scale = Authored(view: view);

        return ((Enabled(view: view) && (RenderGraphExtent.Quantize(fraction: scale) >= 1d))
            ? WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.ThreeQuarter) : scale);
    }
    /// <summary>The saved quality or tier floor, bounded by the view's ceiling.</summary>
    public float Floor(string view) {
        var quality = Quality(view: view);
        var defaults = (Governed(view: view) ? Quality(view: "*") : null);
        var selectedTier = (quality?.Tier ?? GraphTier(view: view));
        var tier = (quality?.RenderScaleFloor ??
            (((selectedTier is { } selected) ? m_resolutionDefinition?.Render.Preset(tier: selected)?.RenderScaleFloor : null) ??
            (defaults?.RenderScaleFloor ??
            (((defaults?.Tier is { } inherited) ? m_resolutionDefinition?.Render.Preset(tier: inherited)?.RenderScaleFloor : null) ??
            WorldRenderScaleTier.Quarter))));

        return Math.Min(val1: WorldRenderScaleTiers.Scale(tier: tier), val2: Ceiling(view: view));
    }
    /// <summary>Whether this view adapts or holds a pin.</summary>
    public bool Enabled(string view) {
        if (m_renderViews.TryGetValue(key: view, value: out var state)) {
            if (state.Pin > 0f) { return true; }
            if (state.Automatic is { } on) { return on; }
        }
        return (Governed(view: view) && ((m_defaultPin > 0f) || DynamicResolution));
    }
    /// <summary>Applies one parsed lever. An invalid pin is refused by name without changing any state.</summary>
    public bool SetResolution(string view, WorldRenderScaleOperation operation, float scale, out string? refusal) {
        refusal = null;
        if (!HasView(view: view)) {
            refusal = $"world.render-scale: unknown view '{view}'";
            return false;
        }
        if ((operation == WorldRenderScaleOperation.Pin) && !CanPin(refusal: out refusal, scale: scale, view: view)) {
            return false;
        }
        if (operation is WorldRenderScaleOperation.Ceiling or WorldRenderScaleOperation.Floor) {
            m_qualityChanged = true;
            var row = (Quality(view: view) ?? new WorldViewQuality(view));

            if (operation == WorldRenderScaleOperation.Ceiling) {
                if (view == "*") {
                    RenderScale = scale;
                    if (Quality(view: view) is { } defaults) {
                        m_renderQuality[view] = defaults with { RenderScale = null };
                    }
                } else {
                    m_renderQuality[view] = row with { RenderScale = scale };
                }
            } else {
                m_renderQuality[view] = row with { RenderScaleFloor = WorldRenderScaleTiers.Nearest(scale: scale) };
            }
        } else if (operation != WorldRenderScaleOperation.Echo) {
            if (view == "*") {
                if (operation != WorldRenderScaleOperation.Pin) {
                    DynamicResolution = (operation != WorldRenderScaleOperation.Off);
                }
                m_defaultPin = ((operation == WorldRenderScaleOperation.Pin) ? scale : 0f);
                Resolution(view: WorldViewGraphs.WorldInstance);
                foreach (var (name, state) in m_renderViews) {
                    if (!IsPlayerView(view: name)) { continue; }
                    if (operation != WorldRenderScaleOperation.Pin) {
                        state.Automatic = (operation != WorldRenderScaleOperation.Off);
                    }
                    state.Pin = ((operation == WorldRenderScaleOperation.Pin) ? scale : 0f);
                }
            } else {
                var state = Resolution(view: view);

                if (operation != WorldRenderScaleOperation.Pin) {
                    state.Automatic = (operation != WorldRenderScaleOperation.Off);
                }
                state.Pin = ((operation == WorldRenderScaleOperation.Pin) ? scale : 0f);
            }
        }
        foreach (var (name, state) in m_renderViews) {
            if (state.Pin > 0f) {
                state.Pin = Math.Clamp(state.Pin, Floor(view: name), Ceiling(view: name));
            }
        }
        m_revision++;
        return true;
    }
    /// <summary>Folds only durable ceilings and floors into the per-view quality vocabulary.</summary>
    public WorldViewDefaults? FoldQuality(WorldViewDefaults? views) => ((!m_qualityChanged || (m_renderQuality.Count == 0))
        ? views : (views ?? WorldViewDefaults.Absent) with { Quality = m_renderQuality.Values.ToArray() });

    // The server's and the client's definitions each reach this reader, so equal rows in another list are the same document
    // quality, and session ceilings and floors survive reading either.
    private static bool SameQuality(IReadOnlyList<WorldViewQuality>? previous, IReadOnlyList<WorldViewQuality>? current) {
        if (ReferenceEquals(objA: previous, objB: current)) { return true; }
        if (((previous?.Count ?? 0) != (current?.Count ?? 0))) { return false; }
        for (var index = 0; (index < (current?.Count ?? 0)); index++) {
            if (!Equals(objA: previous![index], objB: current![index])) { return false; }
        }
        return true;
    }
    // The world's own player views, which the world-wide ceiling, automatic mode and default pin govern: camera and
    // session views keep their native extent unless a row or a lever names them.
    private static bool IsPlayerView(string view) => ((view == WorldViewGraphs.WorldInstance) || (WorldViewNames.ViewOf(instance: view) is not null));
    private static bool Governed(string view) => ((view == "*") || IsPlayerView(view: view));
    private float Authored(string view) => (Quality(view: view)?.RenderScale ??
        (Governed(view: view) ? (Quality(view: "*")?.RenderScale ?? RenderScale) : 1f));
    private WorldViewQuality? Quality(string view) => m_renderQuality.GetValueOrDefault(key: view);
    private QualityTier? GraphTier(string view) {
        if (m_resolutionDefinition?.Views.Graphs is { } graphs) {
            for (var index = 0; (index < graphs.Count); index++) {
                if (graphs[index].Name == view) { return graphs[index].Tier; }
            }
        }
        return null;
    }
}
