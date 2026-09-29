using Puck.SignedDistance;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateRenderSky(WorldDefinition definition, WorldRenderSky? sky, List<string> errors,
        string path = "render.sky", WorldRenderLighting? lighting = null) {
        if (sky?.Layers is not { } layers) { return; }
        var kinds = new HashSet<Type>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < layers.Count; index++) {
            var layer = layers[index];
            var at = $"{path}.layers[{index}]";
            if (layer is null) { errors.Add($"{at} must be a layer."); continue; }
            RenderRowName(layer.Name, at, names, errors);
            if (!kinds.Add(layer.GetType())) { errors.Add($"{at} repeats a layer kind; each kind appears at most once."); }
            switch (layer) {
                case WorldRenderSkyLayer.Gradient gradient:
                    ValidateGradient(gradient, definition, at, errors);
                    break;
                case WorldRenderSkyLayer.Fog fog:
                    RenderScalar(fog.Density, definition, at + ".density", errors, 0f);
                    break;
                case WorldRenderSkyLayer.SunDisc disc:
                    RenderScalar(disc.Radius?.Value, definition, at + ".radius", errors, 0f, MathF.PI / 2f, positive: true);
                    RenderScalar(disc.Intensity, definition, at + ".intensity", errors, 0f);
                    if (disc.Light is { } light && (lighting?.Lights is not { } lights || light < 0 || light >= lights.Count
                        || lights[light] is not WorldRenderLight.Directional)) {
                        errors.Add($"{at}.light must name a directional light's slot in render.lighting.lights.");
                    }
                    break;
                case WorldRenderSkyLayer.Stars stars:
                    if (stars.Density is { } density) { RequirePositive(density, at + ".density", errors); }
                    RenderScalar(stars.Brightness, definition, at + ".brightness", errors, 0f);
                    if (stars.Twinkle is { } twinkle) {
                        RenderScalar(twinkle.Share, definition, at + ".twinkle.share", errors, 0f, 1f);
                        RenderScalar(twinkle.Depth, definition, at + ".twinkle.depth", errors, 0f, 1f);
                        RenderScalar(twinkle.Rate, definition, at + ".twinkle.rate", errors, 0f, positive: true);
                    }
                    break;
                case WorldRenderSkyLayer.Clouds clouds:
                    RenderScalar(clouds.Coverage, definition, at + ".coverage", errors, 0f, 1f);
                    RenderScalar(clouds.Softness, definition, at + ".softness", errors, 0f, 1f, positive: true);
                    RenderScalar(clouds.Scale, definition, at + ".scale", errors, 0f, positive: true);
                    RenderScalar(clouds.Spin, definition, at + ".spin", errors);
                    RenderScalar(clouds.Curl?.Value, definition, at + ".curl", errors);
                    RenderPair(clouds.Drift, definition, at + ".drift", errors);
                    RenderPair(clouds.Shear, definition, at + ".shear", errors);
                    if (clouds.Color is { } color) { RequireBindableColor(color, definition, at + ".color", errors); }
                    break;
            }
        }
    }

    private static void ValidateGradient(WorldRenderSkyLayer.Gradient gradient, WorldDefinition definition, string path, List<string> errors) {
        if (gradient.Stops is not { } stops) { errors.Add($"{path}.stops must carry two to {SdfLighting.MaxSkyStops} stops."); return; }
        if (stops.Count < 2 || stops.Count > SdfLighting.MaxSkyStops) {
            errors.Add($"{path}.stops carries {stops.Count} stops; a gradient carries two to {SdfLighting.MaxSkyStops}.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var values = new WorldValueResolver(definition, default);
        double? previous = null;
        for (var index = 0; index < stops.Count; index++) {
            var stop = stops[index];
            var at = $"{path}.stops[{index}]";
            if (stop is null) { errors.Add($"{at} must be a stop."); continue; }
            RenderRowName(stop.Name, at, names, errors);
            if (stop.Elevation is { } elevation) {
                var before = errors.Count;
                RenderScalar(elevation, definition, at + ".elevation", errors, -1f, 1f);
                if (errors.Count == before) {
                    var resolved = values.Scalar(elevation, 0d);
                    if (previous is { } last && resolved <= last) { errors.Add($"{at}.elevation must exceed the previous stop's."); }
                    previous = resolved;
                }
            } else { errors.Add($"{at}.elevation is required."); }
            if (stop.Color is { } color) { RequireBindableColor(color, definition, at + ".color", errors); }
            else { errors.Add($"{at}.color is required."); }
        }
    }
}
