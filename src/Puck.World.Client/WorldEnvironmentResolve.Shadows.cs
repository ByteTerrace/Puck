using System.Globalization;
using System.Text;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private readonly WorldShadowSelection m_shadows = new();
    private readonly WorldShadowSlot[] m_shadowSlots = new WorldShadowSlot[WorldShadowAllocator.MaxSlots];
    private readonly WorldShadowHandoff[] m_shadowHandoffs = new WorldShadowHandoff[WorldShadowAllocator.MaxFadeSlots];
    private readonly WorldShadowQueued[] m_shadowQueued = new WorldShadowQueued[WorldShadowAllocator.MaxSlots];

    private WorldShadowSelection? m_externalShadows;
    private WorldStateMirror? m_shadowMirror;
    private WorldShadowSettings m_shadowSettings;
    private PresentedTick m_shadowPresented;
    private WorldShadowReadout m_shadowReadout;

    private readonly Lock m_shadowReportGate = new();

    private WorldDefinition? m_shadowReportDefinition;
    private WorldShadowSettings m_shadowReportSettings;

    /// <summary>Gets the complete name-keyed selection feeding the frame's stable slots and active handoffs.</summary>
    public WorldShadowSelection ShadowSlots => (m_externalShadows ?? m_shadows);

    private void PrepareShadows(WorldStateMirror mirror, in WorldShadowSettings settings, WorldShadowSelection? selection) {
        m_externalShadows = selection;
        m_shadowSettings = settings;
        if (selection is not null) {
            Dispose();
            selection.SetSettings(settings: settings);
            return;
        }
        if (!ReferenceEquals(objA: mirror, objB: m_shadowMirror)) {
            Dispose();
            m_shadowMirror = mirror;
            mirror.Delivered += AdvanceShadows;
        }
        m_shadows.SetSettings(settings: settings);
        AdvanceShadows();
    }
    private void AdvanceShadows() {
        if ((m_shadowMirror is { } mirror) && (m_definition is { } definition)) {
            m_shadows.Advance(definition: definition, mirror: mirror, revision: m_revision);
        }
    }
    private void ApplyShadows(SdfLights lights, WorldStateMirror mirror) {
        lock (m_shadowReportGate) {
            var tick = mirror.Presented;
            var settings = m_shadowSettings;
            var readout = ((m_externalShadows is { } selection)
                ? selection.CopyPresented(mirror.PresentationFraction, m_shadowSlots, m_shadowHandoffs, m_shadowQueued, out tick, out settings)
                : m_shadows.CopyTo(handoffs: m_shadowHandoffs, queued: m_shadowQueued, stable: m_shadowSlots, tick: tick));
            var authored = (m_lighting?.Lights ?? WorldRenderLighting.Pinned.Lights!);
            var gpuSlots = lights.ShadowSlots;
            Span<SdfShadowHandoff> controls = stackalloc SdfShadowHandoff[SdfShadowSlots.MaxFadeSlots];
            Span<bool> selected = stackalloc bool[SdfLights.MaxLights];

            selected.Clear();
            gpuSlots.Configure(slots: settings.Slots, fadeCapacity: settings.FadeSlots);

            for (var index = 0; (index < readout.StableCount); index++) {
                var held = m_shadowSlots[index];
                // A newer session delivery may replace its table while this frame still holds the preceding definition.
                var candidate = FrameLight(authored, held.Candidate);

                m_shadowSlots[index] = held with { Candidate = candidate };
                gpuSlots.SetSlot(slot: held.Slot, light: candidate.LightIndex);
                gpuSlots.SetOwner(slot: held.Slot, owner: candidate.Name);
                if (candidate.LightIndex >= 0) { selected[candidate.LightIndex] = true; }
            }
            for (var index = 0; (index < readout.FadeCount); index++) {
                var handoff = m_shadowHandoffs[index];

                handoff = handoff with { Outgoing = FrameLight(authored, handoff.Outgoing), Incoming = FrameLight(authored, handoff.Incoming) };
                m_shadowHandoffs[index] = handoff;
                controls[index] = handoff.Control;
                if (handoff.Incoming.LightIndex >= 0) { selected[handoff.Incoming.LightIndex] = true; }
            }
            gpuSlots.SetHandoffs(handoffs: controls[..readout.FadeCount]);
            for (var index = 0; (index < readout.QueuedCount); index++) {
                var queued = m_shadowQueued[index];

                m_shadowQueued[index] = queued with { Incoming = FrameLight(authored, queued.Incoming) };
            }
            for (var index = 0; (index < lights.Count); index++) {
                var light = lights[index];

                lights.Set(index: index, light: light with { Shadows = (selected[index] ? 1u : 0u) });
            }
            m_shadowReportDefinition = m_definition;
            m_shadowReportSettings = settings;
            m_shadowPresented = tick;
            m_shadowReadout = readout;
        }
    }
    private static WorldShadowCandidate FrameLight(IReadOnlyList<WorldRenderLight> lights, in WorldShadowCandidate candidate) {
        for (var index = 0; (index < lights.Count); index++) {
            if ((lights[index] is WorldRenderLight.Directional { Shadow: WorldShadowMode.Always or WorldShadowMode.Auto } light) &&
                StringComparer.Ordinal.Equals(x: light.Name, y: candidate.Name)) { return candidate with { LightIndex = index }; }
        }
        return candidate with { LightIndex = -1 };
    }
    private void ApplySunDiscLight(SdfLights lights, SdfSky sky) {
        if (m_sky?.Layers is not { } layers) { return; }
        for (var index = 0; (index < layers.Count); index++) {
            if (layers[index] is WorldRenderSkyLayer.SunDisc { Light: null }) {
                sky.Block.DiscLight = ((lights.ShadowSlots[0] >= 0) ? lights.ShadowSlots[0] : FirstDirectional(lights: lights));
                return;
            }
        }
    }

    /// <summary>Formats the last frame's slot owners, tick-derived handoffs and queued crossing reasons.</summary>
    /// <param name="definition">The queried authority's definition; another authority has no report here.</param>
    /// <returns>The slot census, or null before a frame of this definition is presented.</returns>
    public string? DescribeShadowSlots(WorldDefinition definition) {
        lock (m_shadowReportGate) {
            if (!ReferenceEquals(objA: definition, objB: m_shadowReportDefinition)) { return null; }
            var count = m_shadowReadout;
            var text = new StringBuilder();

            text.Append(CultureInfo.InvariantCulture, $"shadowSlots tick={m_shadowPresented.Whole}+{m_shadowPresented.Fraction:0.####} K={m_shadowReportSettings.Slots} F={m_shadowReportSettings.FadeSlots} fadeTicks={m_shadowReportSettings.FadeTicks} overflow={m_shadowReportSettings.Overflow.ToString().ToLowerInvariant()} active={count.MarchSlots} fades={count.FadeCount} queued={count.QueuedCount}");
            for (var index = 0; (index < count.StableCount); index++) {
                var slot = m_shadowSlots[index];

                text.Append(CultureInfo.InvariantCulture, $" | shadow[{slot.Slot}] light={slot.Candidate.Name} index={slot.Candidate.LightIndex} reason=");
                if (slot.Candidate.Mode == WorldShadowMode.Always) { text.Append(value: "always"); } else { text.Append(CultureInfo.InvariantCulture, $"auto rank={slot.Rank}"); }
            }
            for (var index = 0; (index < count.FadeCount); index++) {
                var fade = m_shadowHandoffs[index];

                text.Append(CultureInfo.InvariantCulture, $" | fade[{index}] slot={fade.Slot} outgoing={fade.Outgoing.Name} index={fade.Outgoing.LightIndex} incoming={fade.Incoming.Name} index={fade.Incoming.LightIndex} crossing={fade.CrossingTick} duration={fade.DurationTicks} weight={fade.Weight:R} reason=selectionChanged");
                if ((fade.Flags & WorldShadowHandoffFlags.FromQueue) != 0) { text.Append(value: " fromQueue"); }
            }
            for (var index = 0; (index < count.QueuedCount); index++) {
                var queued = m_shadowQueued[index];

                text.Append(CultureInfo.InvariantCulture, $" | queued[{index}] slot={queued.Slot} incoming={queued.Incoming.Name} index={queued.Incoming.LightIndex} reason={queued.Reason}");
            }
            return text.ToString();
        }
    }
    /// <summary>Releases the delivered-tick subscription when this presentation retires.</summary>
    public void Dispose() {
        if (m_shadowMirror is { } mirror) { mirror.Delivered -= AdvanceShadows; }
        m_shadowMirror = null;
    }
}
