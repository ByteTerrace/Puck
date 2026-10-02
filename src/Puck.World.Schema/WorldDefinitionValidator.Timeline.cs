using Puck.Hosting;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The timeline section: each clock named once in an authorable name, either a tick clock with a period of whole
    // engine ticks or a state clock over a Fixed or Int row, never both, with a positive span and, on a tick clock, a
    // start inside it.
    private static void ValidateTimeline(WorldDefinition definition, List<string> errors) {
        var clocks = (definition.Timeline.Clocks ?? []);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < clocks.Count); index++) {
            var clock = clocks[index];
            var path = $"timeline.clocks[{index}]";

            if (clock is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!RequireUniqueName(
                errors: errors,
                field: "name",
                path: path,
                seen: names,
                value: clock.Name
            )) {
                continue;
            }

            if (!GeneratedName.TryValidateAuthored(
                name: clock.Name,
                reason: out var reservedReason
            )) {
                errors.Add(item: $"{path}.name {reservedReason}");
            }

            if ((clock.SpanSeconds is { } span) && (!double.IsFinite(d: span) || (span <= 0d))) {
                errors.Add(item: $"{path}.spanSeconds must be finite and positive.");
            }

            if (clock.State is { } state) {
                if (clock.PeriodSeconds is not null) {
                    errors.Add(item: $"{path} names both a state row and a period; a clock reads one or the other.");
                }

                if (clock.StartSeconds is not null) {
                    errors.Add(item: $"{path}.startSeconds is refused on a state clock, whose phase is its row's value.");
                }

                var row = definition.State.FirstOrDefault(predicate: candidate => string.Equals(
                    a: candidate.Name.Value,
                    b: state,
                    comparisonType: StringComparison.Ordinal
                ));

                if (row is null) {
                    errors.Add(item: $"{path}.state names no state row '{state}'.");
                } else if (row.Kind is not (CellKind.Fixed or CellKind.Int)) {
                    errors.Add(item: $"{path}.state '{state}' must be a Fixed or Int row.");
                }

                continue;
            }

            if (clock.PeriodSeconds is not { } period) {
                errors.Add(item: $"{path} names neither a period nor a state row.");

                continue;
            }

            if (!WorldClocks.TryWholeTicks(
                seconds: period,
                ticks: out var ticks
            )) {
                errors.Add(item: (((ticks == 0UL) || !double.IsFinite(d: period))
                    ? $"{path}.periodSeconds must be finite and at least one engine tick (1/{EngineTicks.PerSecond} s)."
                    : $"{path}.periodSeconds {period} is not a whole number of engine ticks (1/{EngineTicks.PerSecond} s); the nearest is {(ticks / ((double)EngineTicks.PerSecond))} s."));

                continue;
            }

            if ((clock.StartSeconds is { } start) && (!double.IsFinite(d: start) || (start < 0d) || (start >= clock.Span))) {
                errors.Add(item: $"{path}.startSeconds must be finite and in [0, span).");
            }
        }
    }
    // Every keyed value the document authors, wherever it sits: its clock declared, at least one key, each key's time
    // finite and inside the clock's span, the keys strictly ascending.
    private static void ValidateKeyedValues(WorldDefinition definition, List<string> errors) {
        foreach (var keyed in WorldKeyedValues.Of(definition: definition)) {
            ValidateKeyTimes(
                clockName: keyed.Track.Clock,
                count: keyed.Track.Count,
                definition: definition,
                errors: errors,
                path: keyed.Path,
                timeOf: keyed.Track.AtOf
            );
        }
    }
    private static void ValidateKeyTimes(WorldDefinition definition, string clockName, int count, Func<int, double> timeOf, string path, List<string> errors) {
        if (!WorldKeyResolver.TryClock(
            clock: out var clock,
            name: clockName,
            timeline: definition.Timeline
        )) {
            errors.Add(item: $"{path} keys on clock '{clockName}', which timeline.clocks does not declare.");

            return;
        }

        if (count == 0) {
            errors.Add(item: $"{path} must carry at least one key.");

            return;
        }

        for (var index = 0; (index < count); index++) {
            var at = timeOf(arg: index);

            if (!double.IsFinite(d: at) || (at < 0d) || (at >= clock.Span)) {
                errors.Add(item: $"{path}.keys[{index}].at {at} must be finite and in [0, {clock.Span}), the span of clock '{clock.Name}'.");
            } else if ((index > 0) && (at <= timeOf(arg: (index - 1)))) {
                errors.Add(item: $"{path}.keys[{index}].at {at} must exceed the previous key's.");
            }
        }
    }
    // Expand only admitted section keys. In particular, a null key is a diagnostic, never a dereference in Expand.
    private static (WorldRenderLighting? Lighting, WorldRenderSky? Sky) ValidateAndExpandRenderKeys(WorldDefinition definition, List<string> errors) {
        var before = errors.Count;

        ValidateRenderSectionKeys(definition: definition, errors: errors);

        return ((errors.Count == before)
            ? (WorldRenderKeys.Expand(lighting: definition.Render.Lighting), WorldRenderKeys.Expand(sky: definition.Render.Sky))
            : (definition.Render.Lighting, definition.Render.Sky));
    }
    // A keyed section: its clock and keys together, every key's time as a value's, each key addressing a light or a
    // layer the section names, of the same kind, stating only values a key may move, each a literal, and no field keyed
    // both on its own and by the section.
    private static void ValidateRenderSectionKeys(WorldDefinition definition, List<string> errors) {
        var lighting = definition.Render.Lighting;
        var sky = definition.Render.Sky;

        ValidateSectionNames(
            errors: errors,
            names: (lighting?.Lights ?? []).Select(selector: static light => light?.LightName),
            path: "render.lighting.lights"
        );
        ValidateSectionNames(
            errors: errors,
            names: (sky?.Layers ?? []).Select(selector: static layer => layer?.LayerName),
            path: "render.sky.layers"
        );

        if ((lighting is not null) && ValidateSectionClock(
            clock: lighting.Clock,
            count: (lighting.Keys?.Count ?? 0),
            definition: definition,
            errors: errors,
            path: "render.lighting",
            timeOf: index => (lighting.Keys![index]?.At ?? 0d)
        )) {
            for (var index = 0; (index < lighting.Keys!.Count); index++) {
                var key = lighting.Keys[index];
                var keyPath = $"render.lighting.keys[{index}]";

                if (key is null) {
                    errors.Add(item: $"{keyPath} must be a key.");

                    continue;
                }

                foreach (var (name, part) in (key.Lights ?? new Dictionary<string, WorldRenderLight>())) {
                    var partPath = $"{keyPath}.lights.{name}";
                    var target = lighting.Lights?.FirstOrDefault(predicate: light => string.Equals(
                        a: light?.LightName,
                        b: name,
                        comparisonType: StringComparison.Ordinal
                    ));

                    if (target is null) {
                        errors.Add(item: $"{partPath} names no light; a key addresses a light by the name render.lighting.lights gives it.");

                        continue;
                    }

                    if (part is null) {
                        errors.Add(item: $"{partPath} must be a light.");

                        continue;
                    }

                    if (part.GetType() != target.GetType()) {
                        errors.Add(item: $"{partPath} must keep the kind of the light it names; a key moves values, never a light's kind.");

                        continue;
                    }

                    ValidateLightKeyPart(
                        errors: errors,
                        part: part,
                        path: partPath,
                        target: target
                    );
                }

                if (key.Curvature is { } curvature) {
                    foreach (var (field, value, own) in new (string, BindableScalar?, BindableScalar?)[] {
                        ("cavity", curvature.Cavity, lighting.Curvature?.Cavity),
                        ("rim", curvature.Rim, lighting.Curvature?.Rim),
                        ("ink", curvature.Ink, lighting.Curvature?.Ink),
                        ("inkLow", curvature.InkLow, lighting.Curvature?.InkLow),
                        ("inkHigh", curvature.InkHigh, lighting.Curvature?.InkHigh),
                    }) {
                        RequireKeyScalar(
                            errors: errors,
                            own: own,
                            path: $"{keyPath}.curvature.{field}",
                            value: value
                        );
                    }

                    RequireKeyColor(
                        errors: errors,
                        own: lighting.Curvature?.InkColor,
                        path: $"{keyPath}.curvature.inkColor",
                        value: curvature.InkColor
                    );
                }
            }
        }

        if ((sky is not null) && ValidateSectionClock(
            clock: sky.Clock,
            count: (sky.Keys?.Count ?? 0),
            definition: definition,
            errors: errors,
            path: "render.sky",
            timeOf: index => (sky.Keys![index]?.At ?? 0d)
        )) {
            for (var index = 0; (index < sky.Keys!.Count); index++) {
                var key = sky.Keys[index];
                var keyPath = $"render.sky.keys[{index}]";

                if (key is null) {
                    errors.Add(item: $"{keyPath} must be a key.");

                    continue;
                }

                foreach (var (name, part) in (key.Layers ?? new Dictionary<string, WorldRenderSkyLayer>())) {
                    var partPath = $"{keyPath}.layers.{name}";
                    var target = sky.Layers?.FirstOrDefault(predicate: layer => string.Equals(
                        a: layer?.LayerName,
                        b: name,
                        comparisonType: StringComparison.Ordinal
                    ));

                    if (target is null) {
                        errors.Add(item: $"{partPath} names no layer; a key addresses a layer by the name render.sky.layers gives it.");

                        continue;
                    }

                    if (part is null) {
                        errors.Add(item: $"{partPath} must be a layer.");

                        continue;
                    }

                    if (part.GetType() != target.GetType()) {
                        errors.Add(item: $"{partPath} must keep the kind of the layer it names; a key moves values, never a layer's kind.");

                        continue;
                    }

                    ValidateLayerKeyPart(
                        errors: errors,
                        part: part,
                        path: partPath,
                        target: target
                    );
                }
            }
        }
    }
    private static void ValidateSectionNames(IEnumerable<string?> names, string path, List<string> errors) {
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var index = 0;

        foreach (var name in names) {
            if (name is not null) {
                if (!seen.Add(item: name)) {
                    errors.Add(item: $"{path}[{index}].name '{name}' is duplicated; a key addresses each by its name.");
                }

                if (!GeneratedName.TryValidateAuthored(
                    name: name,
                    reason: out var reason
                )) {
                    errors.Add(item: $"{path}[{index}].name {reason}");
                }
            }

            index++;
        }
    }
    // Returns whether the section carries keys whose clock and times hold.
    private static bool ValidateSectionClock(WorldDefinition definition, string? clock, int count, Func<int, double> timeOf, string path, List<string> errors) {
        if (clock is null) {
            if (count > 0) {
                errors.Add(item: $"{path}.keys need {path}.clock, the clock they read.");
            }

            return false;
        }

        if (count == 0) {
            errors.Add(item: $"{path}.clock '{clock}' keys nothing; a section names a clock only beside its keys.");

            return false;
        }

        var before = errors.Count;

        ValidateKeyTimes(
            clockName: clock,
            count: count,
            definition: definition,
            errors: errors,
            path: path,
            timeOf: timeOf
        );

        return (errors.Count == before);
    }
    private static void ValidateLightKeyPart(WorldRenderLight part, WorldRenderLight target, string path, List<string> errors) {
        RefuseStructure(
            errors: errors,
            path: $"{path}.name",
            stated: (part.LightName is not null)
        );

        switch (part) {
            case WorldRenderLight.Directional directional: {
                    var own = ((WorldRenderLight.Directional)target);

                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.shadows",
                        stated: (directional.Shadows is not null)
                    );
                    RequireKeyDirection(
                        errors: errors,
                        own: own.Direction,
                        path: $"{path}.direction",
                        value: directional.Direction
                    );
                    RequireKeyColor(
                        errors: errors,
                        own: own.Color,
                        path: $"{path}.color",
                        value: directional.Color
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Weight,
                        path: $"{path}.weight",
                        value: directional.Weight
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.AngularRadius?.Value,
                        path: $"{path}.angularRadius",
                        value: directional.AngularRadius?.Value
                    );

                    break;
                }
            case WorldRenderLight.Hemisphere hemisphere: {
                    var own = ((WorldRenderLight.Hemisphere)target);

                    RequireKeyColor(
                        errors: errors,
                        own: own.Color,
                        path: $"{path}.color",
                        value: hemisphere.Color
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Base,
                        path: $"{path}.base",
                        value: hemisphere.Base
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Gradient,
                        path: $"{path}.gradient",
                        value: hemisphere.Gradient
                    );

                    break;
                }
            case WorldRenderLight.Rim rim: {
                    var own = ((WorldRenderLight.Rim)target);

                    RequireKeyColor(
                        errors: errors,
                        own: own.Color,
                        path: $"{path}.color",
                        value: rim.Color
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Weight,
                        path: $"{path}.weight",
                        value: rim.Weight
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Power,
                        path: $"{path}.power",
                        value: rim.Power
                    );

                    break;
                }
            case WorldRenderLight.Point point: {
                    var own = ((WorldRenderLight.Point)target);

                    RequireKeyVector(
                        errors: errors,
                        own: own.Position,
                        path: $"{path}.position",
                        value: point.Position
                    );
                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.anchor",
                        stated: (point.Anchor is not null)
                    );
                    RequireKeyColor(
                        errors: errors,
                        own: own.Color,
                        path: $"{path}.color",
                        value: point.Color
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Radius,
                        path: $"{path}.radius",
                        value: point.Radius
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Weight,
                        path: $"{path}.weight",
                        value: point.Weight
                    );

                    break;
                }
            case WorldRenderLight.Occluder occluder: {
                    var own = ((WorldRenderLight.Occluder)target);

                    RequireKeyVector(
                        errors: errors,
                        own: own.Position,
                        path: $"{path}.position",
                        value: occluder.Position
                    );
                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.anchor",
                        stated: (occluder.Anchor is not null)
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Radius,
                        path: $"{path}.radius",
                        value: occluder.Radius
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Weight,
                        path: $"{path}.weight",
                        value: occluder.Weight
                    );

                    break;
                }
        }
    }
    private static void ValidateLayerKeyPart(WorldRenderSkyLayer part, WorldRenderSkyLayer target, string path, List<string> errors) {
        RefuseStructure(
            errors: errors,
            path: $"{path}.name",
            stated: (part.LayerName is not null)
        );

        switch (part) {
            case WorldRenderSkyLayer.Gradient gradient: {
                    var own = ((WorldRenderSkyLayer.Gradient)target);
                    var ownStops = (own.Stops ?? []);

                    if (gradient.Stops is not { } stops) {
                        break;
                    }

                    if (stops.Count != ownStops.Count) {
                        errors.Add(item: $"{path}.stops carries {stops.Count} stops but the layer carries {ownStops.Count}; a key moves a gradient's stops and never adds or removes one.");

                        break;
                    }

                    var elevations = stops.Count(predicate: static stop => (stop?.Elevation is not null));

                    if ((elevations != 0) && (elevations != stops.Count)) {
                        errors.Add(item: $"{path}.stops states {elevations} of {stops.Count} elevations; a key states every stop's elevation or none, so the stops stay ascending between keys.");
                    }

                    for (var index = 0; (index < stops.Count); index++) {
                        RequireKeyScalar(
                            errors: errors,
                            own: ownStops[index]?.Elevation,
                            path: $"{path}.stops[{index}].elevation",
                            value: stops[index]?.Elevation
                        );
                        RequireKeyColor(
                            errors: errors,
                            own: ownStops[index]?.Color,
                            path: $"{path}.stops[{index}].color",
                            value: stops[index]?.Color
                        );
                    }

                    break;
                }
            case WorldRenderSkyLayer.Fog fog: {
                    RequireKeyScalar(
                        errors: errors,
                        own: ((WorldRenderSkyLayer.Fog)target).Density,
                        path: $"{path}.density",
                        value: fog.Density
                    );

                    break;
                }
            case WorldRenderSkyLayer.SunDisc disc: {
                    var own = ((WorldRenderSkyLayer.SunDisc)target);

                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.light",
                        stated: (disc.Light is not null)
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Radius?.Value,
                        path: $"{path}.radius",
                        value: disc.Radius?.Value
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Intensity,
                        path: $"{path}.intensity",
                        value: disc.Intensity
                    );

                    break;
                }
            case WorldRenderSkyLayer.Stars stars: {
                    var own = ((WorldRenderSkyLayer.Stars)target);

                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.density",
                        stated: (stars.Density is not null)
                    );
                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.seed",
                        stated: (stars.Seed is not null)
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Brightness,
                        path: $"{path}.brightness",
                        value: stars.Brightness
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Twinkle?.Share,
                        path: $"{path}.twinkle.share",
                        value: stars.Twinkle?.Share
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Twinkle?.Depth,
                        path: $"{path}.twinkle.depth",
                        value: stars.Twinkle?.Depth
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Twinkle?.Rate,
                        path: $"{path}.twinkle.rate",
                        value: stars.Twinkle?.Rate
                    );

                    break;
                }
            case WorldRenderSkyLayer.Clouds clouds: {
                    var own = ((WorldRenderSkyLayer.Clouds)target);

                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.seed",
                        stated: (clouds.Seed is not null)
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Coverage,
                        path: $"{path}.coverage",
                        value: clouds.Coverage
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Softness,
                        path: $"{path}.softness",
                        value: clouds.Softness
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Scale,
                        path: $"{path}.scale",
                        value: clouds.Scale
                    );
                    RequireKeyColor(
                        errors: errors,
                        own: own.Color,
                        path: $"{path}.color",
                        value: clouds.Color
                    );
                    RequireKeyVector(
                        errors: errors,
                        own: own.Drift,
                        path: $"{path}.drift",
                        value: clouds.Drift
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Spin,
                        path: $"{path}.spin",
                        value: clouds.Spin
                    );
                    RequireKeyScalar(
                        errors: errors,
                        own: own.Curl?.Value,
                        path: $"{path}.curl",
                        value: clouds.Curl?.Value
                    );
                    RequireKeyVector(
                        errors: errors,
                        own: own.Shear,
                        path: $"{path}.shear",
                        value: clouds.Shear
                    );

                    break;
                }
        }
    }
    // A count, a seed, a kind, a name, a slot or a frame is the shape of what the keys move, so no key states one.
    private static void RefuseStructure(bool stated, string path, List<string> errors) {
        if (stated) {
            errors.Add(item: $"{path} is structure, which a key never states; keys move values, and a count, seed, kind, name, slot or frame is the same at every key.");
        }
    }
    private static void RequireKeyScalar(BindableScalar? value, BindableScalar? own, string path, List<string> errors) {
        if (value is not { } stated) {
            return;
        }

        if (stated.Literal is null) {
            errors.Add(item: $"{path} must be a literal: a section key states the value the field holds at its time, never a binding or keys of its own.");
        }

        RequireUnkeyed(
            errors: errors,
            keyed: (own?.Keys is not null),
            path: path
        );
    }
    private static void RequireKeyColor(BindableColor? value, BindableColor? own, string path, List<string> errors) {
        if (value is not { } stated) {
            return;
        }

        if (stated.Literal is null) {
            errors.Add(item: $"{path} must be a #RRGGBB or #RRGGBBAA literal: a section key states the value the field holds at its time, never a binding or keys of its own.");
        }

        RequireUnkeyed(
            errors: errors,
            keyed: (own?.Keys is not null),
            path: path
        );
    }
    private static void RequireKeyDirection(BindableDirection? value, BindableDirection? own, string path, List<string> errors) {
        if (value is not { } stated) {
            return;
        }

        if (stated.Literal is null) {
            errors.Add(item: $"{path} must be a literal [x, y, z]: a section key states the value the field holds at its time, never keys of its own.");
        }

        RequireUnkeyed(
            errors: errors,
            keyed: (own?.Keys is not null),
            path: path
        );
    }
    private static void RequireKeyVector(BindableVector2? value, BindableVector2? own, string path, List<string> errors) {
        if (value is not { } stated) {
            return;
        }

        if (stated.Literal is null) {
            errors.Add(item: $"{path} must be a literal [x, y]: a section key states the value the field holds at its time, never keys of its own.");
        }

        RequireUnkeyed(
            errors: errors,
            keyed: (own?.Keys is not null),
            path: path
        );
    }
    private static void RequireKeyVector(BindableVector3? value, BindableVector3? own, string path, List<string> errors) {
        if (value is not { } stated) {
            return;
        }

        if (stated.Literal is null) {
            errors.Add(item: $"{path} must be a literal [x, y, z]: a section key states the value the field holds at its time, never keys of its own.");
        }

        RequireUnkeyed(
            errors: errors,
            keyed: (own?.Keys is not null),
            path: path
        );
    }
    private static void RequireUnkeyed(bool keyed, string path, List<string> errors) {
        if (keyed) {
            errors.Add(item: $"{path} is keyed by the section and by its own keys; key a field one way.");
        }
    }
}
