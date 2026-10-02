using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private static int FirstDirectional(SdfLighting environment) {
        for (var index = 0; (index < environment.LightCount); index++) {
            if (environment.GetLight(index: index).Kind == SdfLightKind.Directional) { return index; }
        }
        return -1;
    }
    private void WriteSky(WorldRenderSky? sky, WorldValueResolver values, SdfLighting into) {
        if (sky?.Layers is not { } layers) { return; }
        for (var layerIndex = 0; (layerIndex < layers.Count); layerIndex++) {
            var layer = layers[layerIndex];
            var domain = m_skyDomains[layerIndex];

            switch (layer) {
                case WorldRenderSkyLayer.Fog fog:
                    into.FogDensity = domain.Scalar("density", fog.Density, values, into.FogDensity, WorldValueDomain.Nonnegative);
                    break;
                case WorldRenderSkyLayer.SunDisc disc:
                    into.SunDiscLightIndex = (disc.Light ?? ((into.ShadowLightIndex >= 0)
                        ? into.ShadowLightIndex : FirstDirectional(environment: into)));
                    into.SunDiscRadians = domain.Angle("radius", disc.Radius, values, into.SunDiscRadians, new(0d, (MathF.PI / 2f), MinimumOpen: true));
                    into.SunDiscIntensity = domain.Scalar("intensity", disc.Intensity, values, into.SunDiscIntensity, WorldValueDomain.Nonnegative);
                    into.SkyEnabled = true;
                    break;
            }
        }
    }
}
