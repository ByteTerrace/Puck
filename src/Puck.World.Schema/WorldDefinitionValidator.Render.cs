using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
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
        if (lighting is null) {
            return;
        }

        if (lighting.Lights is { } lights) {
            if (lights.Count > SdfEnvironment.MaxLights) {
                errors.Add(item: $"{path}.lights carries {lights.Count} lights; at most {SdfEnvironment.MaxLights} fit the environment.");
            }

            var shadowing = 0;

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
                                judge: (value, name) => RequireNonNegative(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
                                path: $"{lightPath}.weight",
                                scalar: directional.Weight
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                judge: (value, name) => RequireRange(
                                    errors: errors,
                                    max: MathF.Atan(x: SdfEnvironment.MaxPenumbraSlope),
                                    min: 0f,
                                    name: name,
                                    value: value
                                ),
                                path: $"{lightPath}.angularRadius",
                                scalar: directional.AngularRadius?.Value
                            );

                            if (directional.Shadows == true) {
                                shadowing++;
                            }

                            break;
                        }
                    case WorldRenderLight.Hemisphere hemisphere: {
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                judge: (value, name) => RequireNonNegative(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
                                path: $"{lightPath}.base",
                                scalar: hemisphere.Base
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                judge: (value, name) => RequireFinite(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
                                path: $"{lightPath}.gradient",
                                scalar: hemisphere.Gradient
                            );

                            break;
                        }
                    case WorldRenderLight.Rim rim: {
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                judge: (value, name) => RequireNonNegative(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
                                path: $"{lightPath}.weight",
                                scalar: rim.Weight
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                judge: (value, name) => RequireNonNegative(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
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
                                judge: (value, name) => RequirePositive(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
                                path: $"{lightPath}.radius",
                                scalar: occluder.Radius
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                judge: (value, name) => RequireUnitInterval(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
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
                                judge: (value, name) => RequirePositive(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
                                path: $"{lightPath}.radius",
                                scalar: point.Radius
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                judge: (value, name) => RequireNonNegative(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
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

            if (shadowing > 1) {
                errors.Add(item: $"{path}.lights names {shadowing} shadowing lights; the soft-shadow march runs once per lit pixel, so at most one light shadows.");
            }
        }

        if (lighting.Curvature is { } curvature) {
            foreach (var (name, value) in new (string, BindableScalar?)[] { ("cavity", curvature.Cavity), ("rim", curvature.Rim), ("ink", curvature.Ink), ("inkLow", curvature.InkLow), ("inkHigh", curvature.InkHigh) }) {
                JudgeScalar(
                    definition: definition,
                    errors: errors,
                    judge: (gain, valuePath) => RequireNonNegative(
                        errors: errors,
                        name: valuePath,
                        value: gain
                    ),
                    path: $"{path}.curvature.{name}",
                    scalar: value
                );
            }

            // The outline is a smoothstep across the band, which needs a positive width to have any inside. Judged
            // where the band resolves: an absent end takes the engine default, and a keyed end at each key of either.
            JudgeAtEveryKey(
                definition: definition,
                errors: errors,
                judge: values => ((values[0] < values[1])
                    ? null
                    : $"{path}.curvature.inkLow ({values[0]}) must be below {path}.curvature.inkHigh ({values[1]}){{0}}; an absent end is the engine default."),
                path: $"{path}.curvature",
                scalars: [
                    (curvature.InkLow ?? new BindableScalar(literal: SdfEnvironment.DefaultCurvatureInkLow)),
                    (curvature.InkHigh ?? new BindableScalar(literal: SdfEnvironment.DefaultCurvatureInkHigh)),
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

        var seen = new HashSet<Type>();

        for (var index = 0; (index < layers.Count); index++) {
            var layer = layers[index];
            var layerPath = $"{path}.layers[{index}]";

            if (layer is null) {
                errors.Add(item: $"{layerPath} must be a layer.");

                continue;
            }

            if (!seen.Add(item: layer.GetType())) {
                errors.Add(item: $"{layerPath} repeats a layer kind; each kind appears at most once.");
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
                case WorldRenderSkyLayer.Fog fog: {
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            judge: (value, name) => RequireNonNegative(
                                errors: errors,
                                name: name,
                                value: value
                            ),
                            path: $"{layerPath}.density",
                            scalar: fog.Density
                        );

                        break;
                    }
                case WorldRenderSkyLayer.SunDisc disc: {
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            judge: (value, name) => RequireRange(
                                errors: errors,
                                max: (MathF.PI / 2f),
                                min: 0f,
                                minExclusive: true,
                                name: name,
                                value: value
                            ),
                            path: $"{layerPath}.radius",
                            scalar: disc.Radius?.Value
                        );
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            judge: (value, name) => RequireNonNegative(
                                errors: errors,
                                name: name,
                                value: value
                            ),
                            path: $"{layerPath}.intensity",
                            scalar: disc.Intensity
                        );

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

                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            judge: (value, name) => RequireNonNegative(
                                errors: errors,
                                name: name,
                                value: value
                            ),
                            path: $"{layerPath}.brightness",
                            scalar: stars.Brightness
                        );

                        if (stars.Twinkle is { } twinkle) {
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                judge: (value, name) => RequireUnitInterval(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
                                path: $"{layerPath}.twinkle.share",
                                scalar: twinkle.Share
                            );
                            JudgeScalar(
                                definition: definition,
                                errors: errors,
                                judge: (value, name) => RequireUnitInterval(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
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
                                judge: (value, name) => RequirePositive(
                                    errors: errors,
                                    name: name,
                                    value: value
                                ),
                                path: $"{layerPath}.twinkle.rate",
                                scalar: twinkle.Rate
                            );
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Clouds clouds: {
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            judge: (value, name) => RequireUnitInterval(
                                errors: errors,
                                name: name,
                                value: value
                            ),
                            path: $"{layerPath}.coverage",
                            scalar: clouds.Coverage
                        );
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            judge: (value, name) => RequireRange(
                                errors: errors,
                                max: 1f,
                                min: 0f,
                                minExclusive: true,
                                name: name,
                                value: value
                            ),
                            path: $"{layerPath}.softness",
                            scalar: clouds.Softness
                        );
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            judge: (value, name) => RequirePositive(
                                errors: errors,
                                name: name,
                                value: value
                            ),
                            path: $"{layerPath}.scale",
                            scalar: clouds.Scale
                        );

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
                            judge: (value, name) => RequireFinite(
                                errors: errors,
                                name: name,
                                value: value
                            ),
                            path: $"{layerPath}.spin",
                            scalar: clouds.Spin
                        );
                        JudgeScalar(
                            definition: definition,
                            errors: errors,
                            judge: (value, name) => RequireFinite(
                                errors: errors,
                                name: name,
                                value: value
                            ),
                            path: $"{layerPath}.curl",
                            scalar: clouds.Curl?.Value
                        );

                        break;
                    }
            }
        }
    }
    private static void ValidateGradient(WorldDefinition definition, WorldRenderSkyLayer.Gradient gradient, string path, List<string> errors) {
        if (gradient.Stops is not { } stops) {
            errors.Add(item: $"{path}.stops must carry two to {SdfEnvironment.MaxSkyStops} stops.");

            return;
        }

        if ((stops.Count < 2) || (stops.Count > SdfEnvironment.MaxSkyStops)) {
            errors.Add(item: $"{path}.stops carries {stops.Count} stops; a gradient carries two to {SdfEnvironment.MaxSkyStops}.");
        }

        var elevations = new List<BindableScalar>(capacity: stops.Count);

        for (var stopIndex = 0; (stopIndex < stops.Count); stopIndex++) {
            var stop = stops[stopIndex];
            var stopPath = $"{path}.stops[{stopIndex}]";

            if (stop is null) {
                errors.Add(item: $"{stopPath} must be a stop.");

                continue;
            }

            if (stop.Elevation is { } elevation) {
                if (elevation.State is not null) {
                    errors.Add(item: $"{stopPath}.elevation may not bind a state row: the stops must stay ascending, which a row's value cannot promise.");
                }

                JudgeScalar(
                    definition: definition,
                    errors: errors,
                    judge: (value, name) => RequireRange(
                        errors: errors,
                        max: 1f,
                        min: -1f,
                        name: name,
                        value: value
                    ),
                    path: $"{stopPath}.elevation",
                    scalar: elevation
                );
                elevations.Add(item: elevation);
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

        // The stops must stay strictly ascending wherever they resolve: as authored, and at every key of every keyed
        // elevation.
        JudgeAtEveryKey(
            definition: definition,
            errors: errors,
            judge: values => {
                for (var index = 1; (index < values.Length); index++) {
                    if (values[index] <= values[(index - 1)]) {
                        return $"{path}.stops[{index}].elevation must exceed the previous stop's, and resolves [{string.Join(
                            separator: ", ",
                            values: values
                        )}]{{0}}.";
                    }
                }

                return null;
            },
            path: path,
            scalars: [.. elevations]
        );
    }
    // A bindable scalar's admissibility and every value it authors: a literal judged at its own path, each key's value
    // at its key's.
    private static void JudgeScalar(BindableScalar? scalar, WorldDefinition definition, string path, List<string> errors, Action<float, string> judge) {
        if (scalar is not { } value) {
            return;
        }

        if (!value.IsAuthorable(definition: definition)) {
            errors.Add(item: $"{path} {value} {BindableScalar.Grammar}.");

            return;
        }

        if (value.Literal is { } literal) {
            judge(arg1: literal, arg2: path);
        }

        if (value.Keys is { } keys) {
            for (var index = 0; (index < keys.Keys.Length); index++) {
                judge(arg1: keys.Keys[index].Value, arg2: $"{path}.keys[{index}].value");
            }
        }
    }
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
    // Resolves several scalars together wherever any of them is keyed (WorldKeyResolver, with no live source: a key's
    // own time is its phase) and judges each resolution; a scalar that is a literal or a binding holds its literal (a
    // binding is judged by its own field). Keyed scalars judged together must read one clock, since two clocks' keys
    // meet at times no document states. The judge returns the refusal, with {0} standing for where it resolves.
    private static void JudgeAtEveryKey(WorldDefinition definition, BindableScalar[] scalars, string path, List<string> errors, Func<float[], string?> judge) {
        string? clockName = null;

        foreach (var scalar in scalars) {
            if (scalar.Keys is not { } keys) {
                continue;
            }

            if ((clockName is not null) && !string.Equals(
                a: clockName,
                b: keys.Clock,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path} keys values that must hold an order on clocks '{clockName}' and '{keys.Clock}'; key them on one clock.");

                return;
            }

            clockName = keys.Clock;
        }

        var values = new float[scalars.Length];

        if (clockName is null) {
            for (var index = 0; (index < scalars.Length); index++) {
                if (scalars[index].Literal is not { } literal) {
                    return;
                }

                values[index] = literal;
            }

            if (judge(arg: values) is { } refusal) {
                errors.Add(item: string.Format(
                    format: refusal,
                    arg0: string.Empty,
                    provider: System.Globalization.CultureInfo.InvariantCulture
                ));
            }

            return;
        }

        if (!WorldKeyResolver.TryClock(
            clock: out var clock,
            name: clockName,
            timeline: definition.Timeline
        ) || !double.IsFinite(d: clock.Span) || (clock.Span <= 0d)) {
            return;
        }

        var times = scalars
            .Where(predicate: static scalar => (scalar.Keys is not null))
            .SelectMany(selector: static scalar => scalar.Keys!.Keys.Select(selector: static key => key.At))
            .Where(predicate: at => ((at >= 0d) && (at < clock.Span)))
            .Distinct()
            .Order()
            .ToArray();

        foreach (var at in times) {
            for (var index = 0; (index < scalars.Length); index++) {
                if (scalars[index].Keys is { Count: > 0 } keys) {
                    values[index] = WorldKeyResolver.Scalar(
                        phase: (at / clock.Span),
                        span: clock.Span,
                        track: keys
                    );
                } else if (scalars[index].Literal is { } literal) {
                    values[index] = literal;
                } else {
                    return;
                }
            }

            if (judge(arg: values) is { } refusal) {
                errors.Add(item: string.Format(
                    format: refusal,
                    arg0: $" at {at} on clock '{clock.Name}'",
                    provider: System.Globalization.CultureInfo.InvariantCulture
                ));

                return;
            }
        }
    }
    private static void ValidateRenderEnvironment(WorldDefinition definition, WorldRenderEnvironment? environment, List<string> errors, string path = "render.environment") {
        if (environment is null) {
            return;
        }

        if (environment.Softboxes is { } softboxes) {
            if (softboxes.Count > SdfEnvironment.MaxSoftboxes) {
                errors.Add(item: $"{path}.softboxes carries {softboxes.Count} softboxes; at most {SdfEnvironment.MaxSoftboxes} fit the environment.");
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
