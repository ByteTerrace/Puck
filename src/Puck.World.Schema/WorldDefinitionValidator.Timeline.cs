using Puck.Maths;

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

            if (clock.Anchor is not null) {
                errors.Add(item: $"{path}.anchor is what a projection carries for a state clock it discloses; a document authors a tick clock, a state clock or a view clock.");
            }

            if ((clock.SpanSeconds is { } span) && (!double.IsFinite(d: span) || (span <= 0d))) {
                errors.Add(item: $"{path}.spanSeconds must be finite and positive.");
            }

            if (clock.View is { } view) {
                if ((clock.State is not null) || (clock.PeriodSeconds is not null) || (clock.StartSeconds is not null)) {
                    errors.Add(item: $"{path} names a view beside a state row, a period or a start; a view clock reads where the viewer stands and nothing else.");
                }

                if (!double.IsFinite(d: view.From) || !double.IsFinite(d: view.To) || (view.To <= view.From)) {
                    errors.Add(item: $"{path}.view must measure from a finite 'from' to a finite, greater 'to'.");
                }

                if ((view.Axis?.Value is { } axis) && !(float.IsFinite(f: axis.LengthSquared()) && (axis.LengthSquared() > 0f))) {
                    errors.Add(item: $"{path}.view.axis must be a finite direction of some length; leave it out to measure in every direction.");
                }

                if ((view.Origin?.Value is { } origin) && !float.IsFinite(f: origin.LengthSquared())) {
                    errors.Add(item: $"{path}.view.origin must be finite.");
                }

                continue;
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
                errors.Add(item: $"{path} names neither a period, a state row nor a view.");

                continue;
            }

            if (!WorldClocks.TryWholeTicks(
                seconds: period,
                ticks: out var ticks
            )) {
                errors.Add(item: (((ticks == 0UL) || !double.IsFinite(d: period))
                    ? $"{path}.periodSeconds must be finite and at least one engine tick (1/{FixedTickConversion.TicksPerSecond} s)."
                    : $"{path}.periodSeconds {period} is not a whole number of engine ticks (1/{FixedTickConversion.TicksPerSecond} s); the nearest is {(ticks / ((double)FixedTickConversion.TicksPerSecond))} s."));

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
    private static WorldRenderLighting? ExpandValidatedLighting(WorldDefinition definition, List<string> errors) {
        var lighting = definition.Render.Lighting;
        var start = errors.Count;

        ValidateRenderKeys(definition, errors, "render.lighting", "light", "lights", lighting?.Clock,
            lighting?.Lights, lighting?.Keys, static light => light.LightName, static key => key.At,
            static key => key.Lights, ValidateLightKeyPart,
            (key, path) => ValidateLightingKeyCurvature(key, path, errors));
        return errors.Count == start ? WorldRenderKeys.Expand(lighting) : lighting;
    }

    private static WorldRenderSky? ExpandValidatedSky(WorldDefinition definition, List<string> errors) {
        var sky = definition.Render.Sky;
        var start = errors.Count;

        ValidateRenderKeys(definition, errors, "render.sky", "layer", "layers", sky?.Clock, sky?.Layers, sky?.Keys,
            static layer => layer.LayerName, static key => key.At, static key => key.Layers, ValidateLayerKeyPart);
        return errors.Count == start ? WorldRenderKeys.Expand(sky) : sky;
    }

    // Section keys address a named member, keep its type, and move only the fields that member has not keyed itself.
    private static void ValidateRenderKeys<TKey, TPart>(WorldDefinition definition, List<string> errors, string path,
        string noun, string member, string? clock, IReadOnlyList<TPart>? parts, IReadOnlyList<TKey>? keys,
        Func<TPart, string?> nameOf, Func<TKey, double> timeOf, Func<TKey, IReadOnlyDictionary<string, TPart>?> changesOf,
        Action<TPart, TPart, string, List<string>> validatePart, Action<TKey, string>? validateKey = null)
        where TKey : class where TPart : class {
        ValidateSectionNames((parts ?? []).Select(part => part is null ? null : nameOf(part)), $"{path}.{member}", errors);
        if (!ValidateSectionClock(definition, clock, keys?.Count ?? 0, index => keys![index] is { } key ? timeOf(key) : 0d, path, errors)) {
            return;
        }

        var byName = FirstRowsByName(parts ?? [], nameOf);

        for (var index = 0; index < keys!.Count; index++) {
            var keyPath = $"{path}.keys[{index}]";
            if (keys[index] is not { } key) {
                errors.Add(item: $"{keyPath} must be a key.");
                continue;
            }
            if (changesOf(key) is { } changes) {
                foreach (var (name, part) in changes) {
                    var partPath = $"{keyPath}.{member}.{name}";
                    if (!byName.TryGetValue(name, out var target)) {
                        errors.Add(item: $"{partPath} names no {noun}; a key addresses a {noun} by the name {path}.{member} gives it.");
                    } else if (part is null) {
                        errors.Add(item: $"{partPath} must be a {noun}.");
                    } else if (part.GetType() != target.GetType()) {
                        errors.Add(item: $"{partPath} must keep the kind of the {noun} it names; a key moves values, never a {noun}'s kind.");
                    } else {
                        validatePart(part, target, partPath, errors);
                    }
                }
            }
            validateKey?.Invoke(key, keyPath);
        }
    }

    private static void ValidateLightingKeyCurvature(WorldRenderLightingKey key, string path, List<string> errors) {
        if (key.Curvature is not { } curvature) { return; }
        foreach (var (field, value) in new (string, BindableScalar?)[] {
            ("cavity", curvature.Cavity),
            ("rim", curvature.Rim),
            ("ink", curvature.Ink),
            ("inkLow", curvature.InkLow),
            ("inkHigh", curvature.InkHigh),
        }) {
            RequireKeyScalar(errors: errors, path: $"{path}.curvature.{field}", value: value);
        }

        RequireKeyColor(errors: errors, path: $"{path}.curvature.inkColor", value: curvature.InkColor);
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
                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.shadow",
                        stated: (directional.Shadow is not null)
                    );
                    RequireKeyDirection(
                        errors: errors,
                        path: $"{path}.direction",
                        value: directional.Direction
                    );
                    RequireKeyColor(
                        errors: errors,
                        path: $"{path}.color",
                        value: directional.Color
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.illuminance",
                        value: directional.Illuminance
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.temperature",
                        value: directional.Temperature
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.angularRadius",
                        value: directional.AngularRadius?.Value
                    );

                    break;
                }
            case WorldRenderLight.Rim rim: {
                    RequireKeyColor(
                        errors: errors,
                        path: $"{path}.color",
                        value: rim.Color
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.luminance",
                        value: rim.Luminance
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.power",
                        value: rim.Power
                    );

                    break;
                }
            case WorldRenderLight.Point point: {
                    RequireKeyVector(
                        errors: errors,
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
                        path: $"{path}.color",
                        value: point.Color
                    );
                    RequireKeyScalar(errors: errors, path: $"{path}.intensity", value: point.Intensity);
                    RequireKeyScalar(errors: errors, path: $"{path}.power", value: point.Power);
                    RequireKeyScalar(errors: errors, path: $"{path}.temperature", value: point.Temperature);

                    break;
                }
            case WorldRenderLight.Spot spot: {
                    RequireKeyVector(errors: errors, path: $"{path}.position", value: spot.Position);
                    RequireKeyDirection(errors: errors, path: $"{path}.direction", value: spot.Direction);
                    RefuseStructure(errors: errors, path: $"{path}.anchor", stated: (spot.Anchor is not null));
                    RequireKeyColor(errors: errors, path: $"{path}.color", value: spot.Color);
                    RequireKeyScalar(errors: errors, path: $"{path}.angle", value: spot.Angle?.Value);
                    RequireKeyScalar(errors: errors, path: $"{path}.innerAngle", value: spot.InnerAngle?.Value);
                    RequireKeyScalar(errors: errors, path: $"{path}.intensity", value: spot.Intensity);
                    RequireKeyScalar(errors: errors, path: $"{path}.power", value: spot.Power);
                    RequireKeyScalar(errors: errors, path: $"{path}.temperature", value: spot.Temperature);

                    break;
                }
            case WorldRenderLight.Occluder occluder: {
                    RequireKeyVector(
                        errors: errors,
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
                        path: $"{path}.radius",
                        value: occluder.Radius
                    );
                    RequireKeyScalar(
                        errors: errors,
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
        RefuseStructure(errors: errors, path: $"{path}.blend", stated: (part.Blend is not null));
        RefuseStructure(errors: errors, path: $"{path}.mask", stated: (part.Mask is not null));
        RefuseStructure(errors: errors, path: $"{path}.clock", stated: (part.Clock is not null));
        RefuseStructure(errors: errors, path: $"{path}.visibility", stated: (part.Visibility is not null));
        RefuseStructure(errors: errors, path: $"{path}.tier", stated: (part.Tier is not null));
        RequireKeyScalar(errors: errors, path: $"{path}.opacity", value: part.Opacity);
        RequireKeyScalar(errors: errors, path: $"{path}.luminance", value: part.Luminance);
        RequireKeyScalar(errors: errors, path: $"{path}.transform.turn", value: part.Transform?.Turn?.Value);
        RequireKeyScalar(errors: errors, path: $"{path}.transform.tilt", value: part.Transform?.Tilt?.Value);

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
                            path: $"{path}.stops[{index}].elevation",
                            value: stops[index]?.Elevation
                        );
                        RequireKeyColor(
                            errors: errors,
                            path: $"{path}.stops[{index}].color",
                            value: stops[index]?.Color
                        );
                    }

                    break;
                }
            case WorldRenderSkyLayer.SunDisc disc: {
                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.light",
                        stated: (disc.Light is not null)
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.radius",
                        value: disc.Radius?.Value
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.intensity",
                        value: disc.Intensity
                    );
                    RequireKeyColor(errors: errors, path: $"{path}.color", value: disc.Color);
                    RefuseStructure(errors: errors, path: $"{path}.texture", stated: (disc.Texture is not null));

                    break;
                }
            case WorldRenderSkyLayer.Stars stars: {
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
                    RefuseStructure(errors: errors, path: $"{path}.sparsity", stated: (stars.Sparsity is not null));
                    RefuseStructure(errors: errors, path: $"{path}.size", stated: (stars.Size is not null));
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.brightness",
                        value: stars.Brightness
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.twinkle.share",
                        value: stars.Twinkle?.Share
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.twinkle.depth",
                        value: stars.Twinkle?.Depth
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.twinkle.rate",
                        value: stars.Twinkle?.Rate
                    );

                    break;
                }
            case WorldRenderSkyLayer.Clouds clouds: {
                    RefuseStructure(
                        errors: errors,
                        path: $"{path}.seed",
                        stated: (clouds.Seed is not null)
                    );
                    RefuseStructure(errors: errors, path: $"{path}.octaves", stated: (clouds.Octaves is not null));
                    RefuseStructure(errors: errors, path: $"{path}.warp", stated: (clouds.Warp is not null));
                    RefuseStructure(errors: errors, path: $"{path}.relief", stated: (clouds.Relief is not null));
                    RefuseStructure(errors: errors, path: $"{path}.extinction", stated: (clouds.Extinction is not null));
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.coverage",
                        value: clouds.Coverage
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.softness",
                        value: clouds.Softness
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.scale",
                        value: clouds.Scale
                    );
                    RequireKeyColor(
                        errors: errors,
                        path: $"{path}.color",
                        value: clouds.Color
                    );
                    RequireKeyVector(
                        errors: errors,
                        path: $"{path}.drift",
                        value: clouds.Drift
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.spin",
                        value: clouds.Spin
                    );
                    RequireKeyScalar(
                        errors: errors,
                        path: $"{path}.curl",
                        value: clouds.Curl?.Value
                    );
                    RequireKeyVector(
                        errors: errors,
                        path: $"{path}.shear",
                        value: clouds.Shear
                    );

                    break;
                }
            case WorldRenderSkyLayer.Aurora aurora: {
                    RequireKeyScalar(errors: errors, path: $"{path}.intensity", value: aurora.Intensity);
                    RequireKeyColor(errors: errors, path: $"{path}.color", value: aurora.Color);
                    RequireKeyColor(errors: errors, path: $"{path}.top", value: aurora.Top);
                    RequireKeyScalar(errors: errors, path: $"{path}.base", value: aurora.Base?.Value);
                    RequireKeyScalar(errors: errors, path: $"{path}.height", value: aurora.Height?.Value);
                    RequireKeyScalar(errors: errors, path: $"{path}.fold", value: aurora.Fold?.Value);
                    RefuseStructure(errors: errors, path: $"{path}.rays", stated: (aurora.Rays is not null));
                    RefuseStructure(errors: errors, path: $"{path}.waves", stated: (aurora.Waves is not null));
                    RefuseStructure(errors: errors, path: $"{path}.seed", stated: (aurora.Seed is not null));

                    break;
                }
            case WorldRenderSkyLayer.Noise noise: {
                    RequireKeyColor(errors: errors, path: $"{path}.low", value: noise.Low);
                    RequireKeyColor(errors: errors, path: $"{path}.high", value: noise.High);
                    RequireKeyScalar(errors: errors, path: $"{path}.coverage", value: noise.Coverage);
                    RefuseStructure(errors: errors, path: $"{path}.softness", stated: (noise.Softness is not null));
                    RefuseStructure(errors: errors, path: $"{path}.scale", stated: (noise.Scale is not null));
                    RefuseStructure(errors: errors, path: $"{path}.octaves", stated: (noise.Octaves is not null));
                    RefuseStructure(errors: errors, path: $"{path}.gain", stated: (noise.Gain is not null));
                    RefuseStructure(errors: errors, path: $"{path}.seed", stated: (noise.Seed is not null));

                    break;
                }
            case WorldRenderSkyLayer.Pattern pattern: {
                    var own = ((WorldRenderSkyLayer.Pattern)target);

                    RefuseStructure(errors: errors, path: $"{path}.shape", stated: (pattern.Shape is not null));
                    RefuseStructure(errors: errors, path: $"{path}.cells", stated: (pattern.Cells is not null));
                    RefuseStructure(errors: errors, path: $"{path}.line", stated: (pattern.Line is not null));
                    RefuseStructure(errors: errors, path: $"{path}.softness", stated: (pattern.Softness is not null));
                    if (pattern.Colors is { } colors) {
                        if (colors.Count != (own.Colors?.Count ?? 2)) {
                            errors.Add(item: $"{path}.colors carries {colors.Count} colours; a key moves a pattern's two colours and never adds or removes one.");
                        }
                        for (var index = 0; (index < colors.Count); index++) {
                            RequireKeyColor(errors: errors, path: $"{path}.colors[{index}]", value: colors[index]);
                        }
                    }

                    break;
                }
            case WorldRenderSkyLayer.Panel panel: {
                    RequireKeyColor(errors: errors, path: $"{path}.color", value: panel.Color);
                    RequireKeyScalar(errors: errors, path: $"{path}.intensity", value: panel.Intensity);
                    RequireKeyScalar(errors: errors, path: $"{path}.blur", value: panel.Blur);
                    if ((panel.Direction is not null) || (panel.Size is not null)) {
                        errors.Add(item: $"{path} states panel direction or size, which are structure rather than keyed values.");
                    }
                    break;
                }
            case WorldRenderSkyLayer.Panorama panorama: {
                    RefuseStructure(errors: errors, path: $"{path}.screen", stated: (panorama.Screen is not null));
                    RefuseStructure(errors: errors, path: $"{path}.projection", stated: (panorama.Projection is not null));
                    RequireKeyScalar(errors: errors, path: $"{path}.intensity", value: panorama.Intensity);

                    break;
                }
            case WorldRenderSkyLayer.View view: {
                    RefuseStructure(errors: errors, path: path, stated: ((view.Destination is not null) || (view.Anchor is not null) || (view.Turn is not null) || (view.Scale is not null) || (view.Refresh is not null) || (view.FarDistance is not null) || (view.Shadows is not null) || (view.AmbientOcclusion is not null) || (view.Fallback is not null)));

                    break;
                }
            case WorldRenderSkyLayer.Far far: {
                    RefuseStructure(errors: errors, path: path, stated: ((far.Prototypes is not null) || (far.Anchor is not null) || (far.Turn is not null) || (far.Scale is not null) || (far.Refresh is not null) || (far.FarDistance is not null) || (far.Shadows is not null) || (far.AmbientOcclusion is not null) || (far.Fallback is not null)));

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
    // A section key states the literal a field holds at its time, in the field's own literal form. A member that keys
    // the field itself keeps its own keys (WorldRenderKeys), so a section key never competes with them.
    private static void RequireKeyScalar(BindableScalar? value, string path, List<string> errors) => RequireKeyLiteral(errors: errors, form: "a literal", path: path, stated: (value is { Literal: null }));
    private static void RequireKeyColor(BindableColor? value, string path, List<string> errors) => RequireKeyLiteral(errors: errors, form: "a #RRGGBB or #RRGGBBAA literal", path: path, stated: (value is { Literal: null }));
    private static void RequireKeyDirection(BindableDirection? value, string path, List<string> errors) => RequireKeyLiteral(errors: errors, form: "a literal [x, y, z]", path: path, stated: (value is { Literal: null }));
    private static void RequireKeyVector(BindableVector2? value, string path, List<string> errors) => RequireKeyLiteral(errors: errors, form: "a literal [x, y]", path: path, stated: (value is { Literal: null }));
    private static void RequireKeyVector(BindableVector3? value, string path, List<string> errors) => RequireKeyLiteral(errors: errors, form: "a literal [x, y, z]", path: path, stated: (value is { Literal: null }));
    private static void RequireKeyLiteral(bool stated, string form, string path, List<string> errors) {
        if (stated) {
            errors.Add(item: $"{path} must be {form}: a section key states the value the field holds at its time, never a binding or keys of its own.");
        }
    }
}
