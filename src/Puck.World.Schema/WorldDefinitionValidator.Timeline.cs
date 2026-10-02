using Puck.Hosting;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Each named clock has one source: whole engine ticks, a Fixed or Int state row, or keys on another clock.
    // Every span is positive; a tick clock's start lies inside its span.
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

            if (!GeneratedName.TryValidateDocument(
                name: clock.Name,
                reason: out var reservedReason
            )) {
                errors.Add(item: $"{path}.name {reservedReason}");
            }

            if ((clock.SpanSeconds is { } span) && (!double.IsFinite(d: span) || (span <= 0d))) {
                errors.Add(item: $"{path}.spanSeconds must be finite and positive.");
            }

            if (clock.Phase is not null) {
                if (!WorldValueValidation.TryValidateClock(clock: clock, definition: definition, reason: out var reason)) {
                    errors.Add(item: $"{path} {reason}.");
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
}
