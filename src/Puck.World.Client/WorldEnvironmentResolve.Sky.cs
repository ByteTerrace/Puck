using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private static int FirstDirectional(SdfLighting environment) {
        for (var index = 0; index < environment.LightCount; index++) {
            if (environment.GetLight(index).Kind == SdfLightKind.Directional) { return index; }
        }
        return -1;
    }

    private void WriteSky(WorldRenderSky? sky, WorldValueResolver values, SdfLighting into) {
        if (sky?.Layers is not { } layers) { return; }
        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++) {
            var layer = layers[layerIndex];
            var domain = m_skyDomains[layerIndex];
            switch (layer) {
                case WorldRenderSkyLayer.Gradient { Stops: { } stops }:
                    var count = Math.Min(stops.Count, SdfLighting.MaxSkyStops);
                    for (var index = 0; index < count; index++) {
                        into.SetSkyStop(index, Rgb(values, stops[index]?.Color, Vector3.One),
                            m_stopDomains[index].Scalar("elevation", stops[index]?.Elevation, values, 0f, new(-1d, 1d)));
                    }
                    into.SkyStopCount = count;
                    into.SkyEnabled |= count > 0;
                    break;
                case WorldRenderSkyLayer.Fog fog:
                    into.FogDensity = domain.Scalar("density", fog.Density, values, into.FogDensity, WorldValueDomain.Nonnegative);
                    break;
                case WorldRenderSkyLayer.SunDisc disc:
                    into.SunDiscLightIndex = disc.Light ?? (into.ShadowLightIndex >= 0
                        ? into.ShadowLightIndex : FirstDirectional(into));
                    into.SunDiscRadians = domain.Angle("radius", disc.Radius, values, into.SunDiscRadians, new(0d, MathF.PI / 2f, MinimumOpen: true));
                    into.SunDiscIntensity = domain.Scalar("intensity", disc.Intensity, values, into.SunDiscIntensity, WorldValueDomain.Nonnegative);
                    into.SkyEnabled = true;
                    break;
                case WorldRenderSkyLayer.Stars stars:
                    into.StarDensity = stars.Density ?? into.StarDensity;
                    into.StarBrightness = domain.Scalar("brightness", stars.Brightness, values, into.StarBrightness, WorldValueDomain.Nonnegative);
                    into.StarSeed = stars.Seed ?? into.StarSeed;
                    into.SkyEnabled = true;
                    if (stars.Twinkle is { } twinkle) {
                        into.TwinkleShare = domain.Scalar("twinkle.share", twinkle.Share, values, into.TwinkleShare, WorldValueDomain.Unit);
                        into.TwinkleDepth = domain.Scalar("twinkle.depth", twinkle.Depth, values, into.TwinkleDepth, WorldValueDomain.Unit);
                    }
                    break;
                case WorldRenderSkyLayer.Clouds clouds:
                    into.CloudCoverage = domain.Scalar("coverage", clouds.Coverage, values, into.CloudCoverage, WorldValueDomain.Unit);
                    into.CloudSoftness = domain.Scalar("softness", clouds.Softness, values, into.CloudSoftness, new(0d, 1d, MinimumOpen: true));
                    into.CloudScale = domain.Scalar("scale", clouds.Scale, values, into.CloudScale, WorldValueDomain.Positive);
                    into.CloudSeed = clouds.Seed ?? into.CloudSeed;
                    into.CloudColor = Rgb(values, clouds.Color, into.CloudColor);
                    into.CloudCurl = domain.Angle("curl", clouds.Curl, values, into.CloudCurl, WorldValueDomain.Finite);
                    into.SkyEnabled = true;
                    break;
            }
        }
    }
}
