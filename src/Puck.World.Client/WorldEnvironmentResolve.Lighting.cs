using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private static SdfLight PinnedLight(WorldRenderLight light) => light switch {
        WorldRenderLight.Directional => new(SdfLightKind.Directional, SdfLighting.DefaultSunDirection,
            Vector3.One, SdfLighting.DefaultSunWeight, SdfLighting.DefaultPenumbraSlope, false),
        WorldRenderLight.Hemisphere => new(SdfLightKind.Hemisphere, Vector3.Zero,
            Vector3.One, SdfLighting.DefaultAmbientBase, SdfLighting.DefaultAmbientHemisphere, false),
        WorldRenderLight.Occluder => new(SdfLightKind.Occluder, Vector3.Zero, Vector3.Zero, 0f, 1f, false),
        WorldRenderLight.Point => new(SdfLightKind.Point, Vector3.Zero,
            Vector3.One, SdfLighting.DefaultPointWeight, SdfLighting.DefaultPointRadius, false),
        _ => new(SdfLightKind.Rim, Vector3.Zero, Vector3.One, 0f, SdfLighting.DefaultRimPower, false),
    };
    private void Write(WorldDefinition definition, WorldValueResolver values, SdfLighting into) {
        var lighting = definition.Render.Lighting;

        into.CopyFrom(source: ((lighting?.Lights is null) ? m_default : m_empty));
        if (lighting?.Lights is { } lights) {
            var count = Math.Min(val1: lights.Count, val2: SdfLighting.MaxLights);

            for (var index = 0; (index < count); index++) {
                var authored = lights[index];
                var domain = m_lightDomains[index];
                var previous = PinnedLight(light: authored);

                into.SetLight(index: index, light: authored switch {
                    WorldRenderLight.Directional directional => previous with {
                        Direction = domain.Direction("direction", directional.Direction, values, previous.Direction),
                        Color = Rgb(values, directional.Color, previous.Color),
                        Weight = domain.Scalar("weight", directional.Weight, values, previous.Weight, WorldValueDomain.Nonnegative),
                        Param = ((directional.AngularRadius is { } radius) ? MathF.Tan(x: domain.Angle("angularRadius", radius, values, MathF.Atan(x: previous.Param), new(0d, MathF.Atan(x: SdfLighting.MaxPenumbraSlope)))) : previous.Param),
                        Shadows = (directional.Shadows ?? previous.Shadows),
                    },
                    WorldRenderLight.Hemisphere hemisphere => previous with {
                        Color = Rgb(values, hemisphere.Color, previous.Color),
                        Weight = domain.Scalar("base", hemisphere.Base, values, previous.Weight, WorldValueDomain.Nonnegative),
                        Param = domain.Scalar("gradient", hemisphere.Gradient, values, previous.Param, WorldValueDomain.Finite),
                    },
                    WorldRenderLight.Rim rim => previous with {
                        Color = Rgb(values, rim.Color, previous.Color),
                        Weight = domain.Scalar("weight", rim.Weight, values, previous.Weight, WorldValueDomain.Nonnegative),
                        Param = domain.Scalar("power", rim.Power, values, previous.Param, WorldValueDomain.Nonnegative),
                    },
                    WorldRenderLight.Occluder occluder => previous with {
                        Direction = Position(values, occluder.Position, previous.Direction),
                        Weight = domain.Scalar("weight", occluder.Weight, values, previous.Weight, WorldValueDomain.Unit),
                        Param = domain.Scalar("radius", occluder.Radius, values, previous.Param, WorldValueDomain.Positive),
                    },
                    WorldRenderLight.Point point => previous with {
                        Direction = Position(values, point.Position, previous.Direction),
                        Color = Rgb(values, point.Color, previous.Color),
                        Weight = domain.Scalar("weight", point.Weight, values, previous.Weight, WorldValueDomain.Nonnegative),
                        Param = domain.Scalar("radius", point.Radius, values, previous.Param, WorldValueDomain.Positive),
                    },
                    _ => previous,
                });
            }
            into.LightCount = count;
        }
        if (lighting?.Curvature is { } curvature) {
            into.CurvatureCavity = m_curvatureDomain!.Scalar("cavity", curvature.Cavity, values, into.CurvatureCavity, WorldValueDomain.Nonnegative);
            into.CurvatureRim = m_curvatureDomain!.Scalar("rim", curvature.Rim, values, into.CurvatureRim, WorldValueDomain.Nonnegative);
            into.CurvatureInk = m_curvatureDomain!.Scalar("ink", curvature.Ink, values, into.CurvatureInk, WorldValueDomain.Nonnegative);
            (into.CurvatureInkLow, into.CurvatureInkHigh) = m_curvatureDomain!.Curvature(
                curvature.InkLow, curvature.InkHigh, values, into.CurvatureInkLow, into.CurvatureInkHigh);
            into.CurvatureInkColor = Rgb(values, curvature.InkColor, into.CurvatureInkColor);
        }
        WriteSky(definition.Render.Sky, values, into);
        WriteSkyStack(values, into);
        WriteEnvironment(definition.Render.Environment, values, into);
    }
    private void WriteEnvironment(WorldRenderEnvironment? environment, WorldValueResolver values, SdfLighting into) {
        var count = Math.Min(val1: (environment?.Softboxes?.Count ?? 0), val2: SdfLighting.MaxSoftboxes);

        for (var index = 0; (index < count); index++) {
            var authored = environment!.Softboxes![index];

            into.SetSoftbox(index: index, softbox: new SdfSoftbox(
                Direction: m_softboxDomains[index].Direction("direction", authored.Direction, values, Vector3.UnitY),
                Color: Rgb(values, authored.Color, Vector3.One),
                Weight: m_softboxDomains[index].Scalar("weight", authored.Weight, values, 1f, WorldValueDomain.Nonnegative),
                Size: m_softboxDomains[index].Pair("size", authored.Size, values, Vector2.One, WorldValueDomain.Positive),
                Blur: m_softboxDomains[index].Scalar("blur", authored.Blur, values, 0f, WorldValueDomain.Nonnegative)));
        }
        into.SoftboxCount = count;
        into.HorizonLow = Rgb(values, environment?.Horizon?.Low, Vector3.Zero);
        into.HorizonHigh = Rgb(values, environment?.Horizon?.High, Vector3.Zero);
    }
}
