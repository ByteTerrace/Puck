using System.Numerics;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Reduces delivered light samples to bounded named slot assignments, independently of rendered frames.</summary>
/// <remarks>A session may advance this on its delivery thread. The lock protects the bounded candidate and slot
/// storage; no state mirror is retained or read outside the delivery callback.</remarks>
public sealed class WorldShadowSelection {
    private readonly Lock m_gate = new();
    private readonly WorldShadowAllocator m_allocator = new();
    private readonly WorldShadowCandidate[] m_candidates = new WorldShadowCandidate[SdfLights.MaxLights];

    private WorldRenderLighting? m_authored;
    private WorldRenderLighting? m_lighting;
    private WorldShadowSettings m_settings;
    private int m_count;
    private int m_installs;

    private int m_revision = -1;

    private long m_structure;
    private ulong m_tick;
    private ulong m_previousTick;
    private bool m_initialized;

    /// <summary>Gets the resolved policy, read atomically with respect to delivery.</summary>
    public WorldShadowSettings Settings { get { lock (m_gate) { return m_settings; } } }

    /// <summary>Applies a live quality change using the last delivered candidates, without a fractional crossing.</summary>
    /// <param name="settings">The validated slot policy.</param>
    public void SetSettings(in WorldShadowSettings settings) {
        lock (m_gate) {
            if (m_settings == settings) { return; }
            m_settings = settings;
            if (m_initialized) {
                m_allocator.Advance(m_tick, m_structure, false, m_candidates.AsSpan(length: m_count, start: 0), settings);
                m_previousTick = m_tick;
            }
        }
    }
    /// <summary>Consumes one complete delivery through the existing state and key resolver.</summary>
    /// <param name="definition">The coherent delivered definition.</param>
    /// <param name="revision">Its structural revision.</param>
    /// <param name="mirror">The delivered samples, borrowed only for this call.</param>
    public void Advance(WorldDefinition definition, int revision, WorldStateMirror mirror) {
        lock (m_gate) {
            var authored = definition.Render.Lighting;
            var changed = ((revision != m_revision) || !Equals(objA: m_authored, objB: authored));
            var lighting = (changed ? WorldRenderKeys.Expand(lighting: authored) : m_lighting);
            var reordered = (changed && OnlyLightOrderChanged(after: lighting, before: m_lighting));
            var installed = (mirror.Installs != m_installs);

            if (changed) {
                m_authored = authored;
                m_lighting = lighting;
                m_revision = revision;
                if (reordered) { m_allocator.RemapLights(lights: m_lighting!.Lights!); } else { m_structure++; }
            }
            if (m_initialized && !changed && !installed && (m_tick == mirror.EngineTick)) { return; }
            var lights = (m_lighting?.Lights ?? WorldRenderLighting.Pinned.Lights!);

            m_count = 0;
            for (var index = 0; (index < lights.Count); index++) {
                if (lights[index] is not WorldRenderLight.Directional { Shadow: WorldShadowMode.Always or WorldShadowMode.Auto } light) { continue; }
                var color = ((light.Color is { } authoredColor) ? mirror.Color(authoredColor, Vector4.One, delivered: true) : Vector4.One);
                var weight = ((light.Weight is { } authoredWeight) ? mirror.Scalar(authoredWeight, SdfLights.DefaultSunWeight, delivered: true) : SdfLights.DefaultSunWeight);
                // The light table consumes this same RGB and weight; double intermediates keep finite floats in range.
                var luminance = ((((((double)color.X) * 0.2126) + (((double)color.Y) * 0.7152)) + (((double)color.Z) * 0.0722)) * weight);

                m_candidates[m_count++] = new WorldShadowCandidate(light.Name!, index, index, light.Shadow.Value,
                    (double.IsFinite(d: luminance) ? Math.Max(val1: 0, val2: luminance) : 0));
            }
            var completing = (installed && mirror.LastInstallCompletedDelivery);
            var reset = (!m_initialized || (changed && !reordered) || (installed && !reordered && !completing) || (mirror.EngineTick < m_tick));

            m_allocator.Advance(mirror.EngineTick, m_structure, (installed && !reordered && !completing),
                m_candidates.AsSpan(length: m_count, start: 0), m_settings, completeDelivery: completing);
            if (reset || completing) { m_previousTick = mirror.EngineTick; } else if (mirror.EngineTick != m_tick) { m_previousTick = m_tick; }
            m_tick = mirror.EngineTick;
            m_installs = mirror.Installs;
            m_initialized = true;
        }
    }
    /// <summary>Copies a coherent presented selection into caller-owned storage, without allocating.</summary>
    /// <param name="tick">The presented tick in the latest delivered interval.</param>
    /// <param name="stable">Output slots.</param>
    /// <param name="handoffs">Active incoming marches and tick-derived controls.</param>
    /// <param name="queued">Current desired targets and waiting reasons.</param>
    /// <returns>The complete active stable, fade and queue counts.</returns>
    public WorldShadowReadout CopyTo(in PresentedTick tick, Span<WorldShadowSlot> stable, Span<WorldShadowHandoff> handoffs, Span<WorldShadowQueued> queued) {
        lock (m_gate) { return m_allocator.CopyTo(handoffs: handoffs, queued: queued, stable: stable, tick: tick); }
    }
    /// <summary>Copies slots and their presented tick from one coherent delivered interval, without allocating.</summary>
    /// <param name="fraction">The frame's presentation fraction, clamped to zero through one.</param>
    /// <param name="stable">Output slots.</param>
    /// <param name="handoffs">Active incoming marches and tick-derived controls.</param>
    /// <param name="queued">Current desired targets and waiting reasons.</param>
    /// <param name="tick">The exact tick represented by the copied slots.</param>
    /// <param name="settings">The policy that owns the copied slots.</param>
    /// <returns>The complete active stable, fade and queue counts.</returns>
    public WorldShadowReadout CopyPresented(float fraction, Span<WorldShadowSlot> stable, Span<WorldShadowHandoff> handoffs, Span<WorldShadowQueued> queued, out PresentedTick tick, out WorldShadowSettings settings) {
        lock (m_gate) {
            tick = PresentedTick.Between(current: m_tick, fraction: fraction, previous: m_previousTick);
            settings = m_settings;
            return m_allocator.CopyTo(handoffs: handoffs, queued: queued, stable: stable, tick: tick);
        }
    }

    private static bool OnlyLightOrderChanged(WorldRenderLighting? before, WorldRenderLighting? after) {
        if ((before?.Lights is not { } oldLights) || (after?.Lights is not { } newLights) || (oldLights.Count != newLights.Count) || (newLights.Count > SdfLights.MaxLights) ||
            (before with { Lights = null } != after with { Lights = null })) { return false; }
        var changed = false;
        Span<bool> matched = stackalloc bool[SdfLights.MaxLights];

        matched.Clear();

        for (var index = 0; (index < oldLights.Count); index++) {
            var light = oldLights[index];
            var found = false;

            for (var other = 0; (other < newLights.Count); other++) {
                if (matched[other] || (light != newLights[other])) { continue; }
                matched[other] = true;
                found = true;
                changed |= (index != other);
                break;
            }
            if (!found) { return false; }
        }
        return changed;
    }
}
