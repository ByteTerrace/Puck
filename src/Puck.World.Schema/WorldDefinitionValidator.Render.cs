using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateRenderResolution(WorldDefinition definition, List<string> errors) {
        RequireRange(definition.Render.RenderScale, 0.125f, 1f, "render.renderScale", errors);
        foreach (var tier in Enum.GetValues<Puck.Abstractions.Presentation.QualityTier>()) {
            if (definition.Render.Preset(tier: tier) is { } preset) {
                RequireRange(preset.RenderScale, 0.125f, 1f, $"render.{tier.ToString().ToLowerInvariant()}.renderScale", errors);
            }
        }
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var quality in (definition.Views.Quality ?? [])) {
            if ((quality is null) || string.IsNullOrWhiteSpace(value: quality.Name)) {
                errors.Add(item: "views.quality row requires a view name.");
                continue;
            }
            if (!names.Add(item: quality.Name)) {
                errors.Add(item: $"views.quality repeats view '{quality.Name}'.");
            }
            if (!NamesRenderView(definition: definition, name: quality.Name)) {
                errors.Add(item: $"views.quality row '{quality.Name}' names no render view: use *, world, a camera, a view graph or a generated view name.");
            }
            if (quality.RenderScale is { } scale) {
                RequireRange(scale, 0.125f, 1f, $"views.quality[{quality.Name}].renderScale", errors);
            }
            if ((quality.Tier is { } selected) && (definition.Render.Preset(tier: selected) is null)) {
                errors.Add(item: $"views.quality[{quality.Name}].tier names no {selected} preset.");
            }
        }
    }
    // A row applies to the view it names when that view renders: the default selector, the primary world, an authored
    // camera or view graph, or a view the engine names itself (a generated name, which carries the joiner).
    private static bool NamesRenderView(WorldDefinition definition, string name) {
        if ((name == "*") || (name == WorldViewGraphs.WorldInstance) || name.Contains(value: GeneratedName.Joiner)) { return true; }
        foreach (var camera in definition.Cameras) {
            if (camera.Name == name) { return true; }
        }
        foreach (var graph in (definition.Views.Graphs ?? [])) {
            if (graph.Name == name) { return true; }
        }
        return false;
    }
    // The far distance is the depth every camera march ends at; the band is the representable one (see the constants'
    // remarks), refused by name so a world authoring 0, a negative, or a depth past float's epsilon reach never boots
    // into a renderer whose cone proofs would rest on rounding. Absent resolves to the engine's pinned default.
    private static void ValidateRenderFarDistance(float? farDistance, List<string> errors) {
        if (farDistance is { } value) {
            RequireRange(
                value: value,
                min: WorldRenderDefaults.MinFarDistance,
                max: WorldRenderDefaults.MaxFarDistance,
                name: "render.farDistance",
                errors: errors
            );
        }
    }
    // Judges a section after its keys are expanded (WorldRenderKeys.Expand), so every value a key states is judged as
    // the field it lands in: a literal by itself, a keyed field by each of its keys' values.
    private static void ValidateRenderLighting(WorldDefinition definition, WorldRenderLighting? lighting, List<string> errors, string path = "render.lighting") {
        ValidateShadowPolicies(render: definition.Render, errors: errors);
        if (lighting is null) {
            return;
        }

        if (lighting.Lights is { } lights) {
            if (lights.Count > SdfLights.MaxLights) {
                errors.Add(item: $"{path}.lights carries {lights.Count} lights; at most {SdfLights.MaxLights} fit the lights table.");
            }

            for (var index = 0; (index < lights.Count); index++) {
                var light = lights[index];
                var lightPath = $"{path}.lights[{index}]";

                if (light is null) {
                    errors.Add(item: $"{lightPath} must be a light.");

                    continue;
                }

                switch (light) {
                    case WorldRenderLight.Directional directional: {
                            JudgeDirection(
                                direction: directional.Direction,
                                errors: errors,
                                path: $"{lightPath}.direction"
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.DirectionalWeight,
                                path: $"{lightPath}.weight",
                                scalar: directional.Weight
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.DirectionalAngularRadius,
                                path: $"{lightPath}.angularRadius",
                                scalar: directional.AngularRadius?.Value
                            );

                            if ((directional.Shadow is WorldShadowMode.Always or WorldShadowMode.Auto) && string.IsNullOrWhiteSpace(value: directional.Name)) {
                                errors.Add(item: $"{lightPath}.name is required when shadow is always or auto; a shadow-capable light needs a name.");
                            }

                            break;
                        }
                    case WorldRenderLight.Hemisphere hemisphere: {
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.HemisphereBase,
                                path: $"{lightPath}.base",
                                scalar: hemisphere.Base
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.HemisphereGradient,
                                path: $"{lightPath}.gradient",
                                scalar: hemisphere.Gradient
                            );

                            break;
                        }
                    case WorldRenderLight.Rim rim: {
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.RimWeight,
                                path: $"{lightPath}.weight",
                                scalar: rim.Weight
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.RimPower,
                                path: $"{lightPath}.power",
                                scalar: rim.Power
                            );

                            break;
                        }
                    case WorldRenderLight.Occluder occluder: {
                            JudgePosition(
                                errors: errors,
                                path: $"{lightPath}.position",
                                position: occluder.Position
                            );

                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.OccluderRadius,
                                path: $"{lightPath}.radius",
                                scalar: occluder.Radius
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.OccluderWeight,
                                path: $"{lightPath}.weight",
                                scalar: occluder.Weight
                            );
                            ValidateLightAnchor(
                                anchor: occluder.Anchor,
                                definition: definition,
                                errors: errors,
                                path: $"{lightPath}.anchor"
                            );

                            break;
                        }
                    case WorldRenderLight.Point point: {
                            JudgePosition(
                                errors: errors,
                                path: $"{lightPath}.position",
                                position: point.Position
                            );

                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.PointRadius,
                                path: $"{lightPath}.radius",
                                scalar: point.Radius
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.PointWeight,
                                path: $"{lightPath}.weight",
                                scalar: point.Weight
                            );
                            ValidateLightAnchor(
                                anchor: point.Anchor,
                                definition: definition,
                                errors: errors,
                                path: $"{lightPath}.anchor"
                            );

                            break;
                        }
                }

                if ((LightColor(light: light) is { } color) && !color.IsAuthorable(definition: definition)) {
                    errors.Add(item: $"{lightPath}.color '{color}' {BindableColor.Grammar}.");
                }
            }

        }

        if (lighting.Curvature is { } curvature) {
            foreach (var (field, value) in new (WorldValueField, BindableScalar?)[] { (WorldValueFields.CurvatureCavity, curvature.Cavity), (WorldValueFields.CurvatureRim, curvature.Rim), (WorldValueFields.CurvatureInk, curvature.Ink), (WorldValueFields.CurvatureInkLow, curvature.InkLow), (WorldValueFields.CurvatureInkHigh, curvature.InkHigh) }) {
                JudgeScalar(
                    definition: definition,
                    errors: errors,
                    field: field,
                    path: $"{path}.curvature.{field.Name}",
                    scalar: value
                );
            }

            // The outline is a smoothstep across the band, which needs a positive width to have any inside wherever the
            // band resolves; an absent end takes the engine default.
            JudgeAscending(
                definition: definition,
                errors: errors,
                note: "; an absent end is the engine default",
                path: $"{path}.curvature",
                values: [
                    ((curvature.InkLow ?? new BindableScalar(literal: SdfCurvature.DefaultInkLow)), $"{path}.curvature.inkLow"),
                    ((curvature.InkHigh ?? new BindableScalar(literal: SdfCurvature.DefaultInkHigh)), $"{path}.curvature.inkHigh"),
                ]
            );

            if ((curvature.InkColor is { } inkColor) && !inkColor.IsAuthorable(definition: definition)) {
                errors.Add(item: $"{path}.curvature.inkColor '{inkColor}' {BindableColor.Grammar}.");
            }
        }
    }
    private static void ValidateLightAnchor(WorldAnchor? anchor, WorldDefinition definition, string path, List<string> errors) {
        if (anchor is null) {
            return;
        }

        if (anchor is not (WorldAnchor.Entity or WorldAnchor.EntityPart or WorldAnchor.Placement)) {
            errors.Add(item: $"{path} must name an entity, entity part, or placement frame.");

            return;
        }

        ValidateAnchor(
            anchor: anchor,
            creations: definition.Creations,
            errors: errors,
            path: path,
            placementIds: new HashSet<string>(
                collection: definition.Placements.Select(selector: static placement => placement.Id),
                comparer: StringComparer.Ordinal
            ),
            placements: definition.Placements,
            populationCapacity: definition.Population.Capacity
        );
    }
    private static BindableColor? LightColor(WorldRenderLight light) => (light switch {
        WorldRenderLight.Directional directional => directional.Color,
        WorldRenderLight.Hemisphere hemisphere => hemisphere.Color,
        WorldRenderLight.Rim rim => rim.Color,
        WorldRenderLight.Point point => point.Color,
        _ => null,
    });
    // Judges a section after its keys are expanded, as ValidateRenderLighting does.
    private static void ValidateRenderSky(WorldDefinition definition, WorldRenderSky? sky, List<string> errors, string path = "render.sky", WorldRenderLighting? lighting = null) {
        if (sky?.Layers is not { } layers) {
            return;
        }

        var drawn = 0;
        var runs = new SdfSkyRunCount();

        for (var index = 0; (index < layers.Count); index++) {
            var layer = layers[index];
            var layerPath = $"{path}.layers[{index}]";

            if (layer is null) {
                errors.Add(item: $"{layerPath} must be a layer.");

                continue;
            }
            if (++drawn == (SdfSky.MaxLayers + 1)) {
                errors.Add(item: $"{layerPath} is layer {drawn} of the stack; a sky draws at most {SdfSky.MaxLayers} layers.");
            }
            if ((layer.LayerName is { } name) && SdfSkyDetails.IsFixed(label: name)) {
                errors.Add(item: $"{layerPath}.name '{name}' is a fixed work-counter row's label (a field run's or the atmosphere's); name the layer otherwise.");
            }

            ValidateSkyLayerCommon(definition: definition, errors: errors, layer: layer, path: layerPath);

            var visibility = WorldSkyLayers.VisibilityOf(layer: layer);

            if (
                ((visibility & SdfSkyVisibility.Camera) != 0) &&
                !runs.TryAdd(layerClass: WorldSkyLayers.ClassOf(layer: layer))
            ) {
                errors.Add(item: $"{layerPath} opens a field run past the {SdfSky.MaxUpperFieldRuns} the sky composes above its lowest run; move it beside another field layer, or put fewer point layers between field layers.");
            }

            switch (layer) {
                case WorldRenderSkyLayer.Gradient gradient: {
                        ValidateGradient(
                            definition: definition,
                            errors: errors,
                            gradient: gradient,
                            path: layerPath
                        );

                        break;
                    }
                case WorldRenderSkyLayer.SunDisc disc: {
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            field: WorldValueFields.SunDiscRadius,
                            path: $"{layerPath}.radius",
                            scalar: disc.Radius?.Value
                        );
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            field: WorldValueFields.SunDiscIntensity,
                            path: $"{layerPath}.intensity",
                            scalar: disc.Intensity
                        );

                        JudgeColor(color: disc.Color, definition: definition, errors: errors, path: $"{layerPath}.color");
                        if (disc.Texture is { } texture) {
                            RequireDeclaredScreen(definition: definition, errors: errors, path: $"{layerPath}.texture.screen", screen: texture.Screen);
                        }

                        if (disc.Light is { } lightIndex) {
                            var lights = lighting?.Lights;

                            if ((lights is null) || (lightIndex < 0) || (lightIndex >= lights.Count) || (lights[lightIndex] is not WorldRenderLight.Directional)) {
                                errors.Add(item: $"{layerPath}.light must name a directional light's slot in render.lighting.lights.");
                            }
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Stars stars: {
                        if (stars.Density is { } density) {
                            RequirePositive(
                                errors: errors,
                                name: $"{layerPath}.density",
                                value: density
                            );
                        }

                        if ((stars.Sparsity is { } sparsity) && !((sparsity > 0f) && (sparsity <= 1f))) {
                            errors.Add(item: $"{layerPath}.sparsity {sparsity} lies outside (0, 1].");
                        }
                        if ((stars.Size is { } size) && !((size > 0f) && (size <= 0.5f))) {
                            errors.Add(item: $"{layerPath}.size {size} lies outside (0, 0.5]: a star wider than half its cell straddles cells it is not tested in.");
                        }

                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            field: WorldValueFields.StarBrightness,
                            path: $"{layerPath}.brightness",
                            scalar: stars.Brightness
                        );

                        if (stars.Twinkle is { } twinkle) {
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.TwinkleShare,
                                path: $"{layerPath}.twinkle.share",
                                scalar: twinkle.Share
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.TwinkleDepth,
                                path: $"{layerPath}.twinkle.depth",
                                scalar: twinkle.Depth
                            );
                            JudgeRate(
                                definition: definition,
                                errors: errors,
                                path: $"{layerPath}.twinkle.rate",
                                rate: twinkle.Rate
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                field: WorldValueFields.TwinkleRate,
                                path: $"{layerPath}.twinkle.rate",
                                scalar: twinkle.Rate
                            );
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Aurora aurora: {
                        JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.AuroraIntensity, path: $"{layerPath}.intensity", scalar: aurora.Intensity);
                        JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.AuroraBase, path: $"{layerPath}.base", scalar: aurora.Base?.Value);
                        JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.AuroraHeight, path: $"{layerPath}.height", scalar: aurora.Height?.Value);
                        JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.AuroraFold, path: $"{layerPath}.fold", scalar: aurora.Fold?.Value);
                        JudgeColor(color: aurora.Color, definition: definition, errors: errors, path: $"{layerPath}.color");
                        JudgeColor(color: aurora.Top, definition: definition, errors: errors, path: $"{layerPath}.top");
                        if (aurora.Rays is { } rays) {
                            RequirePositive(errors: errors, name: $"{layerPath}.rays", value: rays);
                        }
                        if (aurora.Waves is { } waves) {
                            RequirePositive(errors: errors, name: $"{layerPath}.waves", value: waves);
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Noise noise: {
                        JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.NoiseCoverage, path: $"{layerPath}.coverage", scalar: noise.Coverage);
                        JudgeColor(color: noise.Low, definition: definition, errors: errors, path: $"{layerPath}.low");
                        JudgeColor(color: noise.High, definition: definition, errors: errors, path: $"{layerPath}.high");
                        if (noise.Scale is { } scale) {
                            RequirePositive(errors: errors, name: $"{layerPath}.scale", value: scale);
                        }
                        if ((noise.Softness is { } softness) && !((softness >= SdfSky.MinCloudSoftness) && (softness <= 1f))) {
                            errors.Add(item: $"{layerPath}.softness {softness} lies outside [{SdfSky.MinCloudSoftness}, 1]: a narrower band leaves the noise's covered edge without two distinct edges.");
                        }
                        if ((noise.Gain is { } gain) && !((gain > 0f) && (gain < 1f))) {
                            errors.Add(item: $"{layerPath}.gain {gain} lies outside (0, 1).");
                        }
                        RequireOctaves(errors: errors, octaves: noise.Octaves, path: $"{layerPath}.octaves");

                        break;
                    }
                case WorldRenderSkyLayer.Pattern pattern: {
                        if ((pattern.Colors is { } colors) && (colors.Count != 2)) {
                            errors.Add(item: $"{layerPath}.colors carries {colors.Count} colours; a pattern paints two.");
                        }
                        for (var colorIndex = 0; (colorIndex < (pattern.Colors?.Count ?? 0)); colorIndex++) {
                            JudgeColor(color: pattern.Colors![colorIndex], definition: definition, errors: errors, path: $"{layerPath}.colors[{colorIndex}]");
                        }
                        if ((pattern.Cells is { } cells) && !((cells >= 1f) && (cells == MathF.Round(x: cells)) && (cells <= 4096f))) {
                            errors.Add(item: $"{layerPath}.cells {cells} must be a whole number from 1 to 4096, so the pattern closes on itself around the sky.");
                        }
                        if ((pattern.Line is { } line) && !((line > 0f) && (line < 1f))) {
                            errors.Add(item: $"{layerPath}.line {line} lies outside (0, 1).");
                        }
                        if ((pattern.Softness is { } patternSoftness) && !((patternSoftness >= 0f) && (patternSoftness <= 0.5f))) {
                            errors.Add(item: $"{layerPath}.softness {patternSoftness} lies outside [0, 0.5].");
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Panorama panorama: {
                        JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.PanoramaIntensity, path: $"{layerPath}.intensity", scalar: panorama.Intensity);
                        if (panorama.Screen is { } screen) {
                            RequireDeclaredScreen(definition: definition, errors: errors, path: $"{layerPath}.screen", screen: screen);
                        } else {
                            errors.Add(item: $"{layerPath}.screen is required: a panorama samples the image a declared screen shows.");
                        }
                        if ((WorldSkyLayers.VisibilityOf(layer: panorama) & SdfSkyVisibility.Lighting) != 0) {
                            errors.Add(item: $"{layerPath}.visibility lets the lighting see a panorama; the environment map binds no screen, so a panorama is seen by the camera alone.");
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Clouds clouds: {
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            field: WorldValueFields.CloudCoverage,
                            path: $"{layerPath}.coverage",
                            scalar: clouds.Coverage
                        );
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            field: WorldValueFields.CloudSoftness,
                            path: $"{layerPath}.softness",
                            scalar: clouds.Softness
                        );
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            field: WorldValueFields.CloudScale,
                            path: $"{layerPath}.scale",
                            scalar: clouds.Scale
                        );

                        RequireOctaves(errors: errors, octaves: clouds.Octaves, path: $"{layerPath}.octaves");
                        if ((clouds.Warp is { } warp) && !(float.IsFinite(f: warp) && (warp >= 0f))) {
                            errors.Add(item: $"{layerPath}.warp {warp} must be finite and not negative.");
                        }
                        if ((clouds.Relief is { } relief) && !(float.IsFinite(f: relief) && (relief >= 0f))) {
                            errors.Add(item: $"{layerPath}.relief {relief} must be finite and not negative.");
                        }
                        if (clouds.Extinction is { } extinction) {
                            RequirePositive(errors: errors, name: $"{layerPath}.extinction", value: extinction);
                        }

                        if ((clouds.Color is { } cloudColor) && !cloudColor.IsAuthorable(definition: definition)) {
                            errors.Add(item: $"{layerPath}.color '{cloudColor}' {BindableColor.Grammar}.");
                        }

                        JudgeVector(
                            errors: errors,
                            path: $"{layerPath}.drift",
                            vector: clouds.Drift
                        );
                        JudgeRate(
                            definition: definition,
                            errors: errors,
                            path: $"{layerPath}.drift",
                            rate: clouds.Drift?.Keys
                        );
                        JudgeVector(
                            errors: errors,
                            path: $"{layerPath}.shear",
                            vector: clouds.Shear
                        );
                        JudgeRate(
                            definition: definition,
                            errors: errors,
                            path: $"{layerPath}.shear",
                            rate: clouds.Shear?.Keys
                        );
                        JudgeRate(
                            definition: definition,
                            errors: errors,
                            path: $"{layerPath}.spin",
                            rate: clouds.Spin
                        );
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            field: WorldValueFields.CloudSpin,
                            path: $"{layerPath}.spin",
                            scalar: clouds.Spin
                        );
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            field: WorldValueFields.CloudCurl,
                            path: $"{layerPath}.curl",
                            scalar: clouds.Curl?.Value
                        );

                        break;
                    }
            }
        }
    }
    // What every layer of the stack carries: its opacity, mask, transform and clock.
    private static void ValidateSkyLayerCommon(WorldDefinition definition, WorldRenderSkyLayer layer, string path, List<string> errors) {
        JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.SkyLayerOpacity, path: $"{path}.opacity", scalar: layer.Opacity);

        if (layer.Transform is { } transform) {
            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.SkyLayerTurn, path: $"{path}.transform.turn", scalar: transform.Turn?.Value);
            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.SkyLayerTilt, path: $"{path}.transform.tilt", scalar: transform.Tilt?.Value);
        }
        if ((layer.Clock is { } clock) && !(definition.Timeline.Clocks ?? []).Any(predicate: candidate => string.Equals(a: candidate?.Name, b: clock, comparisonType: StringComparison.Ordinal))) {
            errors.Add(item: $"{path}.clock '{clock}' names no clock in the timeline section.");
        }
        if (layer.Mask is not { } mask) {
            return;
        }
        if ((mask.Band is null) == (mask.Cone is null)) {
            errors.Add(item: $"{path}.mask states {((mask.Band is null) ? "neither a band nor a cone" : "a band and a cone")}; a mask is one of them.");
        }
        if ((mask.Band is { } band) && ((band.Count != 2) || !band.All(predicate: static angle => (double.IsFinite(d: angle) && (Math.Abs(value: angle) <= (Math.PI / 2d)))) || !(band[0] < band[1]))) {
            errors.Add(item: $"{path}.mask.band must be two ascending elevations from -90deg to 90deg.");
        }
        if ((mask.Cone is { } cone) && (!(cone.Spread > 0d) || (cone.Spread > Math.PI) || (((System.Numerics.Vector3)cone.Toward) == System.Numerics.Vector3.Zero))) {
            errors.Add(item: $"{path}.mask.cone needs a nonzero toward and a spread in (0deg, 180deg].");
        }
        if ((mask.Feather is { } feather) && !(double.IsFinite(d: feather) && (feather >= 0d))) {
            errors.Add(item: $"{path}.mask.feather {feather} must be a finite angle, not negative.");
        }
    }
    private static void JudgeColor(BindableColor? color, WorldDefinition definition, string path, List<string> errors) {
        if ((color is { } value) && !value.IsAuthorable(definition: definition)) {
            errors.Add(item: $"{path} '{value}' {BindableColor.Grammar}.");
        }
    }
    private static void RequireOctaves(uint? octaves, string path, List<string> errors) {
        if ((octaves is { } count) && ((count < 1u) || (count > 8u))) {
            errors.Add(item: $"{path} {count} lies outside 1 to 8.");
        }
    }
    // A screen a sky layer samples is one the world declares, by its surface index.
    private static void RequireDeclaredScreen(WorldDefinition definition, int screen, string path, List<string> errors) {
        if (!definition.Screens.Any(predicate: candidate => (candidate?.Index == screen))) {
            errors.Add(item: $"{path} {screen} names no screen the world declares; a panorama or a textured disc samples a screen's image by its index.");
        }
    }
    private static void ValidateGradient(WorldDefinition definition, WorldRenderSkyLayer.Gradient gradient, string path, List<string> errors) {
        if (gradient.Stops is not { } stops) {
            errors.Add(item: $"{path}.stops must carry two to {SdfSky.MaxStops} stops.");

            return;
        }

        if ((stops.Count < 2) || (stops.Count > SdfSky.MaxStops)) {
            errors.Add(item: $"{path}.stops carries {stops.Count} stops; a gradient carries two to {SdfSky.MaxStops}.");
        }

        var elevations = new List<(BindableScalar Value, string Path)>(capacity: stops.Count);

        for (var stopIndex = 0; (stopIndex < stops.Count); stopIndex++) {
            var stop = stops[stopIndex];
            var stopPath = $"{path}.stops[{stopIndex}]";

            if (stop is null) {
                errors.Add(item: $"{stopPath} must be a stop.");

                continue;
            }

            if (stop.Elevation is { } elevation) {
                JudgeScalar(
                    definition: definition,
                    errors: errors,
                    field: WorldValueFields.StopElevation,
                    path: $"{stopPath}.elevation",
                    scalar: elevation
                );
                elevations.Add(item: (elevation, $"{stopPath}.elevation"));
            } else {
                errors.Add(item: $"{stopPath}.elevation is required.");
            }

            if (stop.Color is { } color) {
                if (!color.IsAuthorable(definition: definition)) {
                    errors.Add(item: $"{stopPath}.color '{color}' {BindableColor.Grammar}.");
                }
            } else {
                errors.Add(item: $"{stopPath}.color is required.");
            }
        }

        // The stops must stay strictly ascending wherever they resolve.
        JudgeAscending(
            definition: definition,
            errors: errors,
            path: $"{path}.stops",
            values: elevations
        );
    }
    // A bindable scalar's admissibility and every value it authors, each judged against its field's one declared domain
    // (WorldValueFields): a literal at its own path, each key's value at its key's. A binding's starting value is judged
    // at load (ValidateBoundStarts).
    private static void JudgeScalar(WorldValueField field, BindableScalar? scalar, WorldDefinition definition, string path, List<string> errors) {
        if (scalar is not { } value) {
            return;
        }

        if (!value.IsAuthorable(definition: definition)) {
            errors.Add(item: $"{path} {value} {BindableScalar.Grammar}.");

            return;
        }

        if ((value.Literal is { } literal) && !field.Domain.Contains(value: literal)) {
            errors.Add(item: $"{path} {literal} must be finite and within {field.Domain}{Because(field: field)}.");
        }

        if (value.Keys is { } keys) {
            for (var index = 0; (index < keys.Keys.Length); index++) {
                if (!field.Domain.Contains(value: keys.Keys[index].Value)) {
                    errors.Add(item: $"{path}.keys[{index}].value {keys.Keys[index].Value} must be finite and within {field.Domain}{Because(field: field)}.");
                }
            }
        }
    }
    // Every presentation scalar a document binds to a state row starts within its field's domain: the value the binding
    // presents as the document loads, which is its row's eased follower unless the binding reads the stored truth with
    // $target. Only a load judges it. A live document's rows hold whatever its rules and console writes last set, which
    // the presentation maps into the domain, so revalidating an applied mutation, a journal replay or an embedded
    // snapshot never reads them.
    private static void ValidateBoundStarts(WorldDefinition definition, List<string> errors) {
        foreach (var bound in WorldKeyedValues.BoundOf(definition: definition)) {
            if (bound is not { Value: { } value, Field: { } field }) {
                continue;
            }

            var (path, binding) = (bound.Path, bound.Binding);

            if (
                (binding.Target
                    ? WorldStateReader.TryReadValue(
                        definition: definition,
                        engineTick: 0UL,
                        key: binding.Key,
                        row: out _,
                        rowName: binding.Row,
                        tick: 0UL,
                        value: out var cell
                    )
                    : WorldStateReader.TryReadEasedValue(
                        definition: definition,
                        engineTick: 0UL,
                        key: binding.Key,
                        row: out _,
                        rowName: binding.Row,
                        tick: 0UL,
                        value: out cell
                    )) &&
                WorldStateReader.TryNumber(
                number: out var start,
                value: cell
            ) &&
                !field.Domain.Contains(value: ((float)start))
            ) {
                errors.Add(item: $"{path} binds {value} whose starting value {((float)start)} lies outside {field.Domain}{Because(field: field)}; a bound value starts within its field's domain.");
            }
        }
    }
    private static string Because(WorldValueField field) => ((field.Why is { } why)
        ? $"; {why}"
        : string.Empty);
    private static void JudgeDirection(BindableDirection? direction, string path, List<string> errors) {
        if (direction is not { } value) {
            return;
        }

        foreach (var vector in value.AuthoredValues()) {
            if (!float.IsFinite(f: vector.X) || !float.IsFinite(f: vector.Y) || !float.IsFinite(f: vector.Z)) {
                errors.Add(item: $"{path} must contain finite coordinates.");
            } else if (vector.LengthSquared() <= 0f) {
                errors.Add(item: $"{path} must be nonzero.");
            }
        }
    }
    private static void JudgePosition(BindableVector3? position, string path, List<string> errors) {
        if (position is not { } value) {
            return;
        }

        foreach (var vector in value.AuthoredValues()) {
            if (!VectorFunctions.IsFinite(vector: vector)) {
                errors.Add(item: $"{path} must contain finite coordinates.");
            }
        }
    }
    private static void JudgeVector(BindableVector2? vector, string path, List<string> errors) {
        if (vector is not { } value) {
            return;
        }

        foreach (var component in value.AuthoredValues()) {
            if (!float.IsFinite(f: component.X) || !float.IsFinite(f: component.Y)) {
                errors.Add(item: $"{path} must contain finite coordinates.");
            }
        }
    }
    // A rate is integrated over the tick, so it may never bind a state row, whose value can move by any amount between
    // two ticks, and its keys read a tick clock, whose phase is a function of the tick.
    private static void JudgeRate(BindableScalar? rate, WorldDefinition definition, string path, List<string> errors) {
        if (rate?.Binding is not null) {
            errors.Add(item: $"{path} is a rate the tick integrates and may not bind a state row: a row's value can move by any amount between two ticks, so the offset would jump; key it on a tick clock instead.");
        }

        JudgeRate(
            definition: definition,
            errors: errors,
            path: path,
            rate: rate?.Keys
        );
    }
    private static void JudgeRate(IWorldKeyTrack? rate, WorldDefinition definition, string path, List<string> errors) {
        if (
            (rate is not null) &&
            WorldKeyResolver.TryClock(
            clock: out var clock,
            name: rate.Clock,
            timeline: definition.Timeline
        ) &&
            clock.IsStateClock
        ) {
            errors.Add(item: $"{path} is a rate the tick integrates and may key only on a tick clock; clock '{clock.Name}' reads state row '{clock.State}', whose history the integral would depend on.");
        }
    }
    // Every value the atmosphere carries within its field's domain, and every colour in its grammar. The kinds are
    // structure, so the section carries no keys of its own; each value keys on a clock alone.
    private static void ValidateRenderAtmosphere(WorldDefinition definition, WorldRenderAtmosphere? atmosphere, List<string> errors, string path = "render.atmosphere") {
        if (atmosphere is null) {
            return;
        }

        if (atmosphere.Fog is { } fog) {
            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.FogDensity, path: $"{path}.fog.density", scalar: fog.Density);
            JudgeColor(color: fog.Color, definition: definition, errors: errors, path: $"{path}.fog.color");
            JudgeHeight(definition: definition, errors: errors, height: fog.Height, path: $"{path}.fog.height");
        }
        if (atmosphere.Haze is { } haze) {
            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.HazeAmount, path: $"{path}.haze.amount", scalar: haze.Amount);
            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.HazeAnisotropy, path: $"{path}.haze.anisotropy", scalar: haze.Anisotropy);
            JudgeHeight(definition: definition, errors: errors, height: haze.Height, path: $"{path}.haze.height");
        }
        if (atmosphere.Medium is { } medium) {
            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.MediumSurface, path: $"{path}.medium.surface", scalar: medium.Surface);
            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.MediumExtinction, path: $"{path}.medium.extinction", scalar: medium.Extinction);
            JudgeColor(color: medium.Color, definition: definition, errors: errors, path: $"{path}.medium.color");
        }

        static void JudgeHeight(WorldRenderAirHeight? height, WorldDefinition definition, string path, List<string> errors) {
            if (height is null) {
                return;
            }

            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.AirBase, path: $"{path}.base", scalar: height.Base);
            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.AirFalloff, path: $"{path}.falloff", scalar: height.Falloff);
        }
        static void JudgeColor(BindableColor? color, WorldDefinition definition, string path, List<string> errors) {
            if ((color is { } authored) && !authored.IsAuthorable(definition: definition)) {
                errors.Add(item: $"{path} '{authored}' {BindableColor.Grammar}.");
            }
        }
    }
    private static void ValidateRenderEnvironment(WorldDefinition definition, WorldRenderEnvironment? environment, List<string> errors, string path = "render.environment") {
        if (environment is null) {
            return;
        }

        if (environment.Softboxes is { } softboxes) {
            if (softboxes.Count > SdfSky.MaxSoftboxes) {
                errors.Add(item: $"{path}.softboxes carries {softboxes.Count} softboxes; at most {SdfSky.MaxSoftboxes} fit the softbox table.");
            }

            for (var index = 0; (index < softboxes.Count); index++) {
                var softbox = softboxes[index];
                var softboxPath = $"{path}.softboxes[{index}]";

                if (softbox is null) {
                    errors.Add(item: $"{softboxPath} must be a softbox.");

                    continue;
                }

                var direction = softbox.Direction;

                if (
                    !float.IsFinite(f: direction.X) ||
                    !float.IsFinite(f: direction.Y) ||
                    !float.IsFinite(f: direction.Z)
                ) {
                    errors.Add(item: $"{softboxPath}.direction must contain finite coordinates.");
                } else if ((((direction.X * direction.X) + (direction.Y * direction.Y)) + (direction.Z * direction.Z)) <= 0f) {
                    errors.Add(item: $"{softboxPath}.direction must be nonzero.");
                }

                var size = softbox.Size;

                if (
                    !float.IsFinite(f: size.X) ||
                    !float.IsFinite(f: size.Y)
                ) {
                    errors.Add(item: $"{softboxPath}.size must contain finite coordinates.");
                } else if (
                    (size.X <= 0f) ||
                    (size.Y <= 0f)
                ) {
                    errors.Add(item: $"{softboxPath}.size must be strictly positive on both axes.");
                }

                if (softbox.Weight is { } weight) {
                    RequireNonNegative(
                        errors: errors,
                        name: $"{softboxPath}.weight",
                        value: weight
                    );
                }

                if (softbox.Blur is { } blur) {
                    RequireNonNegative(
                        errors: errors,
                        name: $"{softboxPath}.blur",
                        value: blur
                    );
                }

                if (
                    (softbox.Color is { } color) &&
                    !color.IsAuthorable(definition: definition)
                ) {
                    errors.Add(item: $"{softboxPath}.color '{color}' {BindableColor.Grammar}.");
                }
            }
        }

        if (environment.Horizon is { } horizon) {
            if (
                (horizon.Low is { } low) &&
                !low.IsAuthorable(definition: definition)
            ) {
                errors.Add(item: $"{path}.horizon.low '{low}' {BindableColor.Grammar}.");
            }

            if (
                (horizon.High is { } high) &&
                !high.IsAuthorable(definition: definition)
            ) {
                errors.Add(item: $"{path}.horizon.high '{high}' {BindableColor.Grammar}.");
            }
        }
    }
}
