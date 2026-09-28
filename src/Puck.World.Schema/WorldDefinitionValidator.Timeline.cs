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
    private static void ValidateRenderCycle(WorldDefinition definition, List<string> errors) {
        if (definition.Render.Cycle is not { } cycle) {
            return;
        }

        var row = definition.State.FirstOrDefault(predicate: candidate => string.Equals(
            a: candidate.Name.Value,
            b: cycle.State,
            comparisonType: StringComparison.Ordinal
        ));

        if (row is null) {
            errors.Add(item: $"render.cycle.state names no state row '{cycle.State}'.");
        } else if (row.Kind is not (CellKind.Fixed or CellKind.Int)) {
            errors.Add(item: $"render.cycle.state '{cycle.State}' must be a Fixed or Int row.");
        }

        if (cycle.Keys is not { Count: >= 2 }) {
            errors.Add(item: "render.cycle.keys must carry at least two keys.");

            return;
        }

        // A key over an unauthored section moves the PINNED topology (the sun and hemisphere; the two-stop gradient
        // and the fog), which is what the cycle track resolves it against.
        var lightingShape = ResolvedLightingShape(lighting: definition.Render.Lighting);
        var skyShape = (definition.Render.Sky ?? WorldRenderSky.Pinned);

        for (var index = 0; (index < cycle.Keys.Count); index++) {
            var key = cycle.Keys[index];
            var path = $"render.cycle.keys[{index}]";

            if (
                !float.IsFinite(f: key.At) ||
                (key.At < 0f) ||
                (key.At >= 1f)
            ) {
                errors.Add(item: $"{path}.at must be finite and in [0, 1).");
            } else if (
                (index > 0) &&
                (key.At <= cycle.Keys[(index - 1)].At)
            ) {
                errors.Add(item: $"{path}.at must exceed the previous key's.");
            }

            // A cloud layer's drift, shear and spin are rates the clock integrates; a state row's value can move by any
            // amount between two ticks, so a rate keyed on it would jump the layer.
            foreach (var clouds in (key.Sky?.Layers ?? []).OfType<WorldRenderSkyLayer.Clouds>()) {
                if ((clouds.Drift is not null) || (clouds.Shear is not null) || (clouds.Spin is not null)) {
                    errors.Add(item: $"{path}.sky clouds may not key drift, shear or spin: each is a rate the tick integrates, and a key on a state row would jump the layer; author it on the static sky.");
                }
            }

            ValidateRenderLighting(
                definition: definition,
                errors: errors,
                lighting: key.Lighting,
                path: $"{path}.lighting",
                shape: lightingShape
            );
            ValidateRenderSky(
                definition: definition,
                errors: errors,
                path: $"{path}.sky",
                sky: key.Sky,
                shape: skyShape,
                lighting: lightingShape
            );
        }

        ValidateRenderCycleResolution(
            cycle: cycle,
            errors: errors,
            lightingShape: lightingShape,
            skyShape: skyShape
        );
    }
}
