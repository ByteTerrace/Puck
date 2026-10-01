namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // How a keyed scalar moves across an interval with no key of its own inside: held, affine in time (Linear), or
    // eased by smoothstep over one segment, named by its two keys' times so two such pieces compare as one function.
    private enum OrderPieceShape {
        Held,
        Affine,
        Smooth,
    }
    // A scalar over one open interval between consecutive key times of an ordered set: its value at the interval's
    // start (every keyed value is continuous from the right, so this is also its limit there), its limit at the
    // interval's end from the left, and its shape between. Every ease is monotone on a segment (Linear, smoothstep,
    // whose slope 6t(1 - t) is never negative, and Step, which holds until the segment's end), so the piece lies
    // between its two ends throughout.
    private readonly record struct OrderPiece(float Start, float End, OrderPieceShape Shape, double WindowFrom, double WindowTo);

    // Scalars that must stay strictly ascending wherever they resolve: a gradient's stop elevations, an ink band's
    // ends. A literal holds its value. Keyed scalars must read one clock, since two clocks' keys meet at phases no
    // document states, and a bound scalar is refused, since a row's value promises no order.
    //
    // A keyed set is judged over the whole phase circle. Its key times partition it; at each, the values resolve
    // exactly and are compared. Between two consecutive times, no value has a key, so each moves monotonically (see
    // OrderPiece). Where the difference of two neighbours is itself monotone (either holds, both are affine in time, or
    // both ease by smoothstep over the same window), it lies between its two ends, so the pair is refused when the
    // lower can reach the upper at the interval's end. Otherwise the lower's largest end must stay below the upper's
    // smallest. Both rules are sound: neither admits a pair that touches or crosses; the second may refuse a pair that
    // never does.
    private static void JudgeAscending(WorldDefinition definition, IReadOnlyList<(BindableScalar Value, string Path)> values, string path, List<string> errors, string note = "") {
        var judged = true;

        foreach (var (value, valuePath) in values) {
            if (value.State is not null) {
                errors.Add(item: $"{valuePath} may not bind a state row: {path} must stay ascending, which a row's value cannot promise.");
                judged = false;
            } else if ((value.Literal is null) && (value.Keys is null)) {
                // A malformed binding is refused by its own field.
                judged = false;
            }
        }

        if (!judged || (values.Count < 2)) {
            return;
        }

        string? clockName = null;

        foreach (var (value, _) in values) {
            if (value.Keys is not { } keys) {
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

        if (clockName is null) {
            for (var index = 1; (index < values.Count); index++) {
                var lower = values[(index - 1)].Value.Literal!.Value;
                var upper = values[index].Value.Literal!.Value;

                if (!(lower < upper)) {
                    errors.Add(item: AscendingRefusal(
                        lower: values[(index - 1)].Path,
                        note: note,
                        upper: values[index].Path,
                        where: $"the value below is {lower} and the value above {upper}"
                    ));

                    return;
                }
            }

            return;
        }

        if (!WorldKeyResolver.TryClock(
            clock: out var clock,
            name: clockName,
            timeline: definition.Timeline
        ) || !double.IsFinite(d: clock.Span) || (clock.Span <= 0d)) {
            // An unknown clock or span is refused by the timeline and the key times.
            return;
        }

        var span = clock.Span;
        var times = new SortedSet<double>();

        foreach (var (value, _) in values) {
            if (value.Keys is not { } keys) {
                continue;
            }

            for (var index = 0; (index < keys.Count); index++) {
                var at = keys.AtOf(index: index);

                if (!double.IsFinite(d: at) || (at < 0d) || (at >= span) || ((index > 0) && (at <= keys.AtOf(index: (index - 1))))) {
                    // Malformed key times are refused by ValidateKeyTimes.
                    return;
                }

                _ = times.Add(item: at);
            }
        }

        var points = times.ToArray();
        var resolved = new float[values.Count];

        foreach (var at in points) {
            for (var index = 0; (index < values.Count); index++) {
                resolved[index] = ((values[index].Value.Keys is { } keys)
                    ? WorldKeyResolver.Scalar(
                        phase: (at / span),
                        span: span,
                        track: keys
                    )
                    : values[index].Value.Literal!.Value);
            }

            for (var index = 1; (index < values.Count); index++) {
                if (!(resolved[(index - 1)] < resolved[index])) {
                    errors.Add(item: AscendingRefusal(
                        lower: values[(index - 1)].Path,
                        note: note,
                        upper: values[index].Path,
                        where: $"at {at} on clock '{clock.Name}' the value below resolves {resolved[(index - 1)]} and the value above {resolved[index]}"
                    ));

                    return;
                }
            }
        }

        if (JudgeBetweenKeys(
            clock: clock,
            note: note,
            points: points,
            values: values
        ) is { } refusal) {
            errors.Add(item: refusal);
        }
    }
    // The open intervals between consecutive key times, the last wrapping through the end of the span into the first.
    private static string? JudgeBetweenKeys(WorldClock clock, double[] points, IReadOnlyList<(BindableScalar Value, string Path)> values, string note) {
        var span = clock.Span;
        var pieces = new OrderPiece[values.Count];

        for (var point = 0; (point < points.Length); point++) {
            var from = points[point];
            var wraps = ((point + 1) == points.Length);
            var to = points[((point + 1) % points.Length)];
            var length = (wraps
                ? ((1d - (from / span)) + (to / span))
                : ((to / span) - (from / span))
            );

            for (var index = 0; (index < values.Count); index++) {
                pieces[index] = PieceOf(
                    from: from,
                    length: length,
                    span: span,
                    to: to,
                    value: values[index].Value
                );
            }

            for (var index = 1; (index < values.Count); index++) {
                var lower = pieces[(index - 1)];
                var upper = pieces[index];
                var monotone = (
                    (lower.Shape == OrderPieceShape.Held) ||
                    (upper.Shape == OrderPieceShape.Held) ||
                    ((lower.Shape == OrderPieceShape.Affine) && (upper.Shape == OrderPieceShape.Affine)) ||
                    ((lower.Shape == OrderPieceShape.Smooth) && (upper.Shape == OrderPieceShape.Smooth) && (lower.WindowFrom == upper.WindowFrom) && (lower.WindowTo == upper.WindowTo))
                );

                var (reach, fall) = (monotone
                    ? (lower.End, upper.End)
                    : (Math.Max(val1: lower.Start, val2: lower.End), Math.Min(val1: upper.Start, val2: upper.End))
                );

                if (reach >= fall) {
                    return AscendingRefusal(
                        lower: values[(index - 1)].Path,
                        note: note,
                        upper: values[index].Path,
                        where: $"between {from} and {to} on clock '{clock.Name}' the value below can reach {reach} where the value above can fall to {fall}"
                    );
                }
            }
        }

        return null;
    }
    // A scalar's piece over the open interval from one key time, of a phase length, to the next.
    private static OrderPiece PieceOf(BindableScalar value, double span, double from, double to, double length) {
        if (value.Keys is not { } track) {
            var literal = value.Literal!.Value;

            return new OrderPiece(
                End: literal,
                Shape: OrderPieceShape.Held,
                Start: literal,
                WindowFrom: 0d,
                WindowTo: 0d
            );
        }

        var keys = track.Keys;
        var segment = WorldKeyResolver.Segment(
            phase: (from / span),
            span: span,
            track: track
        );
        var a = keys[segment.From].Value;
        var b = keys[segment.To].Value;
        var ease = keys[segment.From].Ease;
        var start = WorldKeyResolver.Scalar(
            phase: (from / span),
            span: span,
            track: track
        );

        if ((keys.Length == 1) || (ease == WorldEase.Step) || (a == b)) {
            // Step holds its key's value up to the segment's end, so its limit there from the left is still that value.
            return new OrderPiece(
                End: start,
                Shape: OrderPieceShape.Held,
                Start: start,
                WindowFrom: 0d,
                WindowTo: 0d
            );
        }

        // The interval ends at the segment's end exactly when its end is the next key's time; otherwise the segment
        // runs on past it.
        var fraction = ((keys[segment.To].At == to)
            ? 1d
            : Math.Min(
                val1: 1d,
                val2: ((segment.Offset + length) / segment.Length)
            ));

        return new OrderPiece(
            End: ((float)(a + ((b - a) * WorldKeyResolver.Ease(
                ease: ease,
                t: fraction
            )))),
            Shape: ((ease == WorldEase.Smooth)
                ? OrderPieceShape.Smooth
                : OrderPieceShape.Affine),
            Start: start,
            WindowFrom: keys[segment.From].At,
            WindowTo: keys[segment.To].At
        );
    }
    // The one refusal of an ordered pair that resolves out of order somewhere on its clock.
    private static string AscendingRefusal(string lower, string upper, string where, string note) => $"{upper} must exceed {lower} wherever they resolve; {where}{note}.";
}
