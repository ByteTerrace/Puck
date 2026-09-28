using Puck.SignedDistance;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void RenderScalar(BindableScalar? value, WorldDefinition definition, string path, List<string> errors,
        float min = float.NegativeInfinity, float max = float.PositiveInfinity, bool positive = false) {
        if (value is not { } scalar) { return; }
        RequireBindableScalar(scalar, definition, path, errors);
        RequireBindableDomain(scalar, definition, path, new WorldValueDomain(min, max, MinimumOpen: positive), errors);
    }

    private static void RenderVector(BindableVector3? value, WorldDefinition definition, string path, List<string> errors) {
        if (value is not { } vector) { return; }
        if (vector.Keys is { } keys && !WorldValueValidation.TryValidate(keys, definition, out var reason)) {
            errors.Add($"{path} {reason}.");
        } else if (!vector.IsAuthorable(definition)) { errors.Add($"{path} must contain finite coordinates or valid numeric bindings and keys."); }
    }

    private static void RenderDirection(BindableDirection? value, WorldDefinition definition, string path, List<string> errors) {
        if (value is not { } direction) { return; }
        if (direction.Keys is { } keys && !WorldValueValidation.TryValidate(keys, definition, out var reason)) {
            errors.Add($"{path} {reason}.");
        } else if (!direction.IsAuthorable(definition)) { errors.Add($"{path} must contain finite coordinates and be nonzero at every authored direction."); }
    }

    private static void RenderPair(BindableVector2? value, WorldDefinition definition, string path, List<string> errors, bool positive = false) {
        if (value is not { } pair) { return; }
        if (pair.Keys is { } keys && !WorldValueValidation.TryValidate(keys, definition, out var reason)) {
            errors.Add($"{path} {reason}.");
            return;
        }
        void Components(BindableVector2 vector, string at) {
            var minimum = positive ? 0f : float.NegativeInfinity;
            RenderScalar(vector.X, definition, at + "[0]", errors, minimum, positive: positive);
            RenderScalar(vector.Y, definition, at + "[1]", errors, minimum, positive: positive);
        }
        if (pair.Keys?.Keys is { } rows) {
            for (var index = 0; index < rows.Count; index++) {
                if (rows[index] is { } key) { Components(key.Value, $"{path}.keys[{index}].value"); }
            }
        } else { Components(pair, path); }
    }

    private static WorldRenderLighting ResolvedLightingShape(WorldRenderLighting? lighting) => lighting switch {
        { Lights: not null } authored => authored,
        { } curvatureOnly => curvatureOnly with { Lights = WorldRenderLighting.Pinned.Lights },
        null => WorldRenderLighting.Pinned,
    };

    private static void ValidateRenderFarDistance(float? farDistance, List<string> errors) {
        if (farDistance is { } value) {
            RequireRange(value, WorldRenderDefaults.MinFarDistance, WorldRenderDefaults.MaxFarDistance, "render.farDistance", errors);
        }
    }

    private static void RenderRowName(string? name, string path, HashSet<string> seen, List<string> errors) {
        if (name is not null) { RequireUniqueName(value: name, path: path, field: "name", seen: seen, errors: errors); }
    }

    private static void RenderLightAnchor(WorldAnchor? anchor, WorldDefinition definition, string path, List<string> errors) {
        if (anchor is null) { return; }
        if (anchor is not (WorldAnchor.Entity or WorldAnchor.EntityPart or WorldAnchor.Placement)) {
            errors.Add($"{path} must name an entity, entity part, or placement frame.");
            return;
        }
        ValidateAnchor(anchor, definition.Placements, new HashSet<string>(definition.Placements.Select(static row => row.Id), StringComparer.Ordinal),
            definition.Creations, definition.Population.Capacity, path, errors);
    }

    private static void ValidateRenderLighting(WorldDefinition definition, WorldRenderLighting? lighting, List<string> errors, string path = "render.lighting") {
        if (lighting is null) { return; }
        if (lighting.Lights is { } lights) {
            if (lights.Count > SdfEnvironment.MaxLights) { errors.Add($"{path}.lights carries {lights.Count} lights; at most {SdfEnvironment.MaxLights} fit the environment."); }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var shadowing = 0;
            for (var index = 0; index < lights.Count; index++) {
                var light = lights[index];
                var at = $"{path}.lights[{index}]";
                if (light is null) { errors.Add($"{at} must be a light."); continue; }
                RenderRowName(light.Name, at, names, errors);
                BindableColor? color = null;
                switch (light) {
                    case WorldRenderLight.Directional sun:
                        RenderDirection(sun.Direction, definition, at + ".direction", errors);
                        RenderScalar(sun.Weight, definition, at + ".weight", errors, 0f);
                        RenderScalar(sun.AngularRadius?.Value, definition, at + ".angularRadius", errors, 0f, MathF.Atan(SdfEnvironment.MaxPenumbraSlope));
                        if (sun.Shadows == true) { shadowing++; }
                        color = sun.Color;
                        break;
                    case WorldRenderLight.Hemisphere fill:
                        RenderScalar(fill.Base, definition, at + ".base", errors, 0f);
                        RenderScalar(fill.Gradient, definition, at + ".gradient", errors);
                        color = fill.Color;
                        break;
                    case WorldRenderLight.Rim rim:
                        RenderScalar(rim.Weight, definition, at + ".weight", errors, 0f);
                        RenderScalar(rim.Power, definition, at + ".power", errors, 0f);
                        color = rim.Color;
                        break;
                    case WorldRenderLight.Point point:
                        RenderVector(point.Position, definition, at + ".position", errors);
                        RenderScalar(point.Radius, definition, at + ".radius", errors, 0f, positive: true);
                        RenderScalar(point.Weight, definition, at + ".weight", errors, 0f);
                        RenderLightAnchor(point.Anchor, definition, at + ".anchor", errors);
                        color = point.Color;
                        break;
                    case WorldRenderLight.Occluder occluder:
                        RenderVector(occluder.Position, definition, at + ".position", errors);
                        RenderScalar(occluder.Radius, definition, at + ".radius", errors, 0f, positive: true);
                        RenderScalar(occluder.Weight, definition, at + ".weight", errors, 0f, 1f);
                        RenderLightAnchor(occluder.Anchor, definition, at + ".anchor", errors);
                        break;
                }
                if (color is { } bound) { RequireBindableColor(bound, definition, at + ".color", errors); }
            }
            if (shadowing > 1) { errors.Add($"{path}.lights names {shadowing} shadowing lights; at most one light shadows."); }
        }
        if (lighting.Curvature is not { } curvature) { return; }
        var curvatureErrors = errors.Count;
        RenderScalar(curvature.Cavity, definition, path + ".curvature.cavity", errors, 0f);
        RenderScalar(curvature.Rim, definition, path + ".curvature.rim", errors, 0f);
        RenderScalar(curvature.Ink, definition, path + ".curvature.ink", errors, 0f);
        RenderScalar(curvature.InkLow, definition, path + ".curvature.inkLow", errors, 0f);
        RenderScalar(curvature.InkHigh, definition, path + ".curvature.inkHigh", errors, 0f);
        if (errors.Count != curvatureErrors) { return; }
        var values = new WorldValueResolver(definition, default);
        var low = curvature.InkLow is { } lowValue ? (float)values.Scalar(lowValue, SdfEnvironment.DefaultCurvatureInkLow) : SdfEnvironment.DefaultCurvatureInkLow;
        var high = curvature.InkHigh is { } highValue ? (float)values.Scalar(highValue, SdfEnvironment.DefaultCurvatureInkHigh) : SdfEnvironment.DefaultCurvatureInkHigh;
        if (low >= high) { errors.Add($"{path}.curvature.inkLow ({low}) must be below {path}.curvature.inkHigh ({high}); an absent end is the engine default."); }
        if (curvature.InkColor is { } ink) { RequireBindableColor(ink, definition, path + ".curvature.inkColor", errors); }
    }

    private static void ValidateRenderEnvironment(WorldDefinition definition, WorldRenderEnvironment? environment, List<string> errors, string path = "render.environment") {
        if (environment is null) { return; }
        if (environment.Softboxes is { } softboxes) {
            if (softboxes.Count > SdfEnvironment.MaxSoftboxes) { errors.Add($"{path}.softboxes carries {softboxes.Count} softboxes; at most {SdfEnvironment.MaxSoftboxes} fit the environment."); }
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < softboxes.Count; index++) {
                var box = softboxes[index];
                var at = $"{path}.softboxes[{index}]";
                if (box is null) { errors.Add($"{at} must be a softbox."); continue; }
                RenderRowName(box.Name, at, names, errors);
                RenderDirection(box.Direction, definition, at + ".direction", errors);
                RenderPair(box.Size, definition, at + ".size", errors, positive: true);
                RenderScalar(box.Weight, definition, at + ".weight", errors, 0f);
                RenderScalar(box.Blur, definition, at + ".blur", errors, 0f);
                if (box.Color is { } color) { RequireBindableColor(color, definition, at + ".color", errors); }
            }
        }
        if (environment.Horizon?.Low is { } low) { RequireBindableColor(low, definition, path + ".horizon.low", errors); }
        if (environment.Horizon?.High is { } high) { RequireBindableColor(high, definition, path + ".horizon.high", errors); }
    }
}
