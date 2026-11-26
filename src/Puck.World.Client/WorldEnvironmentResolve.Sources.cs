using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    // A directional light that comes from a body (WorldRenderLightSource), lowered once per render section: where the
    // body stands, the conics it rides in order, and what the light and its disc take from it.
    private readonly record struct LightSource(int Light, Vector3 Focus, SourceOrbit[] Orbits, float Radius, float Distance, bool DerivesSoftness);
    // One conic a source rides and the clock whose phase is its turn.
    private readonly record struct SourceOrbit(FixedConicOrbit Conic, string Clock, ulong PhaseOffset);

    private LightSource[] m_sources = [];

    // The sourced lights of a lighting section. A source with an orbit whose elements the closed form refuses (the
    // validator names them) leaves its light where its authored direction puts it.
    private static LightSource[] SourcesOf(WorldRenderLighting? lighting) {
        if (lighting?.Lights is not { } lights) {
            return [];
        }

        List<LightSource>? sources = null;

        for (var index = 0; (index < Math.Min(val1: lights.Count, val2: SdfLights.MaxLights)); index++) {
            if (lights[index] is not WorldRenderLight.Directional { Source: { } source } directional) {
                continue;
            }

            var authored = (source.Orbits ?? []);
            var orbits = new SourceOrbit[Math.Min(val1: authored.Count, val2: WorldRenderLightSource.MaxOrbits)];
            var admitted = true;

            for (var ordinal = 0; (admitted && (ordinal < orbits.Length)); ordinal++) {
                admitted = (
                    (authored[ordinal] is { Clock: not null } orbit) &&
                    float.IsFinite(f: orbit.SemiMajorAxis) && (orbit.SemiMajorAxis > 0f) &&
                    float.IsFinite(f: orbit.Eccentricity) && (orbit.Eccentricity >= 0f) && (FixedQ4816.FromDouble(value: orbit.Eccentricity) < FixedConicOrbit.MaxEccentricity) &&
                    float.IsFinite(f: orbit.InclinationDegrees) && float.IsFinite(f: orbit.AscendingNodeDegrees) && float.IsFinite(f: orbit.PeriapsisDegrees) && float.IsFinite(f: orbit.Phase)
                );
                if (admitted) {
                    var stated = authored[ordinal];

                    orbits[ordinal] = new SourceOrbit(
                        Clock: stated.Clock,
                        Conic: FixedConicOrbit.FromAngles(
                            argumentOfPeriapsis: FixedQ4816.FromDegrees(degrees: stated.PeriapsisDegrees),
                            ascendingNode: FixedQ4816.FromDegrees(degrees: stated.AscendingNodeDegrees),
                            eccentricity: FixedQ4816.FromDouble(value: stated.Eccentricity),
                            inclination: FixedQ4816.FromDegrees(degrees: stated.InclinationDegrees),
                            semiMajorAxis: FixedQ4816.FromDouble(value: stated.SemiMajorAxis)
                        ),
                        PhaseOffset: TurnOf(turn: stated.Phase)
                    );
                }
            }

            if (!admitted) {
                continue;
            }

            (sources ??= []).Add(item: new LightSource(
                DerivesSoftness: (directional.AngularRadius is null),
                Distance: (source.Distance ?? ((authored.Count > 0) ? authored[0].SemiMajorAxis : 0f)),
                Focus: (source.Position?.Value ?? Vector3.Zero),
                Light: index,
                Orbits: orbits,
                Radius: MathF.Max(x: 0f, y: source.Radius)
            ));
        }

        return (sources?.ToArray() ?? []);
    }
    // A fraction of a turn as the conic reads it: its fractional part scaled by 2^64.
    private static ulong TurnOf(double turn) => unchecked((ulong)((turn - Math.Floor(d: turn)) * 18446744073709551616d));

    // Places each sourced light from where its body and the frame's first view stand, every call: the body rides its
    // clocks and the viewer moves between resolutions, so neither is part of the cached resolution. A light one of
    // whose clocks reads no phase, or a mirror no view has drawn from, keeps the light as resolved.
    private void ApplySources(SdfLights lights, SdfSky sky, WorldStateMirror mirror) {
        if ((m_sources.Length == 0) || (mirror.ViewPoint is not { } viewer)) {
            return;
        }

        foreach (var source in m_sources) {
            if (source.Light >= lights.Count) {
                continue;
            }

            var position = source.Focus;
            var placed = true;

            foreach (var orbit in source.Orbits) {
                placed = mirror.TryPhase(clock: out _, name: orbit.Clock, phase: out var phase);
                if (!placed) {
                    break;
                }

                position += orbit.Conic.PositionAt(phase: unchecked((TurnOf(turn: phase) + orbit.PhaseOffset))).ToVector3();
            }

            if (!placed) {
                continue;
            }

            var toward = (position - viewer);
            var distance = toward.Length();

            if (!(distance > 1e-3f) || !float.IsFinite(f: distance)) {
                continue;
            }

            // Inside the body the light is the surface's: the distance holds at the radius.
            var reach = MathF.Max(x: distance, y: source.Radius);
            var light = lights[source.Light];

            light.Direction = (toward / distance);
            if (source.Distance > 0f) {
                var share = (source.Distance / reach);

                light.Weight *= (share * share);
            }

            var sine = ((source.Radius > 0f) ? MathF.Min(x: 1f, y: (source.Radius / reach)) : 0f);

            if (source.DerivesSoftness && (sine > 0f)) {
                // The penumbra half-slope is the tangent of the body's angular radius, within what the shadow gather
                // admits.
                light.Param = MathF.Min(x: SdfLights.MaxPenumbraSlope, y: (sine / MathF.Sqrt(x: MathF.Max(x: 1e-6f, y: (1f - (sine * sine))))));
            }

            lights.Set(index: source.Light, light: light);
            if (sine > 0f) {
                SizeDiscs(angularRadius: MathF.Asin(x: sine), light: source.Light, sky: sky);
            }
        }
    }
    // A disc about a sourced light that authors no radius shows the body's angular radius.
    private void SizeDiscs(SdfSky sky, int light, float angularRadius) {
        if (m_sky?.Layers is not { } layers) { return; }
        for (var index = 0; ((index < layers.Count) && (index < m_skyLayerIndex.Length)); index++) {
            if ((layers[index] is WorldRenderSkyLayer.SunDisc { Radius: null }) && (m_skyLayerIndex[index] >= 0)) {
                ref var disc = ref sky.Parameters<SdfSkyDisc>(index: m_skyLayerIndex[index]);

                if (disc.Light == light) { disc.Radius = angularRadius; }
            }
        }
    }
}
