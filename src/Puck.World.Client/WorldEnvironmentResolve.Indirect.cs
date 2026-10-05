using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private WorldResolvedIndirect m_indirect = new(SdfIndirectGains.One, null, SdfIndirectApplication.Default);

    private void WriteIndirect(WorldStateMirror mirror, WorldRenderIndirect? indirect) {
        var sources = indirect?.Sources;
        var sourceSite = new WorldValueSite("render.indirect.sources");
        var applySite = new WorldValueSite("render.indirect.apply");
        m_indirect = new WorldResolvedIndirect(new SdfIndirectGains(
            Lights: Scalar(mirror, sources?.Lights, 1f, WorldValueFields.IndirectLights, sourceSite),
            Emission: Scalar(mirror, sources?.Emission, 1f, WorldValueFields.IndirectEmission, sourceSite),
            Screens: Scalar(mirror, sources?.Screens, 1f, WorldValueFields.IndirectScreens, sourceSite),
            Sky: Scalar(mirror, sources?.Sky, 1f, WorldValueFields.IndirectSky, sourceSite),
            Feedback: Scalar(mirror, sources?.Feedback, 1f, WorldValueFields.IndirectFeedback, sourceSite)),
            indirect?.Bounces,
            new SdfIndirectApplication(
                Intensity: Scalar(mirror, indirect?.Apply?.Intensity, 1f, WorldValueFields.IndirectIntensity, applySite),
                Tint: Rgb(mirror, indirect?.Apply?.Tint, Vector3.One),
                Contact: Scalar(mirror, indirect?.Apply?.Contact, 1f, WorldValueFields.IndirectContact, applySite)));
    }
}

/// <summary>The immutable indirect controls resolved through the environment's existing bindings and clocks.</summary>
/// <param name="Gains">Source gains applied at each origin.</param>
/// <param name="Bounces">Requested finite feedback depth, or the selected tier's default.</param>
/// <param name="Apply">Receiver-only controls.</param>
public readonly record struct WorldResolvedIndirect(SdfIndirectGains Gains, int? Bounces, SdfIndirectApplication Apply);
