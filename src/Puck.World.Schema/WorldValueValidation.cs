namespace Puck.World;

/// <summary>Validation shared by the scalar, color and geometric bindables. Curves name clocks rather than carrying
/// their own time source, and validation follows the same clock graph that resolution reads.</summary>
public static class WorldValueValidation {
    /// <summary>Checks a curve's clock, ordered key positions and values.</summary>
    /// <typeparam name="T">The bindable value type.</typeparam>
    /// <param name="curve">The curve.</param>
    /// <param name="definition">The owning world.</param>
    /// <param name="validateValue">The field type's validation.</param>
    /// <returns>Whether every key and its clock are authorable.</returns>
    public static bool IsAuthorable<T>(WorldKeys<T> curve, WorldDefinition definition, Func<T, WorldDefinition, bool> validateValue) {
        if (!TryValidate(curve, definition, out _)) { return false; }
        for (var index = 0; index < curve.Keys.Count; index++) {
            if (!validateValue(curve.Keys[index].Value, definition)) { return false; }
        }
        return true;
    }

    /// <summary>Checks a curve's clock and key positions, preserving the clock name in refusals.</summary>
    /// <typeparam name="T">The bindable value type.</typeparam>
    /// <param name="curve">The curve.</param>
    /// <param name="definition">The owning world.</param>
    /// <param name="reason">The refusal, or an empty string.</param>
    /// <returns>Whether the curve's structure and clock are authorable.</returns>
    public static bool TryValidate<T>(WorldKeys<T> curve, WorldDefinition definition, out string reason) {
        var resolver = new WorldValueResolver(definition, default);
        if (resolver.Clock(curve.Clock) is not { } clock) {
            reason = $"names no clock '{curve.Clock}'";
            return false;
        }
        if (!TryValidateClock(clock, definition, out reason)) { return false; }
        if (curve.Keys is not { Count: >= 2 }) {
            reason = "requires at least two keys";
            return false;
        }
        for (var index = 0; index < curve.Keys.Count; index++) {
            var key = curve.Keys[index];
            if (key is null || !double.IsFinite(key.At) || key.At < 0d || key.At >= clock.Span
                || (index > 0 && key.At <= curve.Keys[index - 1].At)) {
                reason = $"key {index} must have a finite, strictly ascending at in [0, {clock.Span}) for clock '{clock.Name}'";
                return false;
            }
            if (!Enum.IsDefined(key.Ease)) {
                reason = $"key {index} has an unknown ease";
                return false;
            }
            if (Nested(key.Value)) {
                reason = $"key {index} contains nested keyed values; compose clocks through timeline phase instead";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    private static bool Nested<T>(T value) => value switch {
        BindableScalar scalar => scalar.Keys is not null,
        BindableColor color => color.Keys is not null,
        BindableAngle angle => Nested(angle.Value),
        BindableVector2 vector => vector.Keys is not null || Nested(vector.X) || Nested(vector.Y),
        BindableVector3 vector => vector.Keys is not null || Nested(vector.X) || Nested(vector.Y) || Nested(vector.Z),
        BindableDirection direction => direction.Keys is not null || Nested(direction.Value),
        _ => false,
    };

    /// <summary>Checks a clock's source graph, refusing cycles before any phase is evaluated.</summary>
    /// <param name="clock">The clock.</param>
    /// <param name="definition">The owning world.</param>
    /// <param name="reason">The refusal, or an empty string.</param>
    /// <returns>Whether the clock has a finite, acyclic source.</returns>
    public static bool TryValidateClock(WorldClock clock, WorldDefinition definition, out string reason) =>
        ValidateClock(clock, new WorldValueResolver(definition, default), new HashSet<string>(StringComparer.Ordinal), out reason);

    private static bool ValidateClock(WorldClock clock, WorldValueResolver resolver, HashSet<string> active, out string reason) {
        if (!active.Add(clock.Name)) {
            reason = $"clock '{clock.Name}' forms a cycle";
            return false;
        }
        if (!double.IsFinite(clock.Span) || clock.Span <= 0d) {
            reason = $"clock '{clock.Name}' must have a finite positive span";
            return false;
        }
        if (clock.Phase is { } phase) {
            if (clock.PeriodSeconds is not null || clock.State is not null || clock.StartSeconds is not null || phase.Keys is not { } keys) {
                reason = $"clock '{clock.Name}' phase must be keys on another clock, with no period, state or start";
                return false;
            }
            if (resolver.Clock(keys.Clock) is not { } parent) {
                reason = $"clock '{clock.Name}' names no clock '{keys.Clock}'";
                return false;
            }
            if (!ValidateClock(parent, resolver, active, out reason)) { return false; }
            if (keys.Keys is not { Count: >= 2 }) {
                reason = $"clock '{clock.Name}' phase requires at least two keys";
                return false;
            }
            for (var index = 0; index < keys.Keys.Count; index++) {
                var key = keys.Keys[index];
                if (key is null || !double.IsFinite(key.At) || key.At < 0d || key.At >= parent.Span
                    || (index > 0 && key.At <= keys.Keys[index - 1].At) || !Enum.IsDefined(key.Ease)
                    || key.Value.Literal is not { } literal || !float.IsFinite(literal) || literal < 0f || literal > 1f) {
                    reason = $"clock '{clock.Name}' phase keys require ordered positions in '{parent.Name}' and literal phases in [0, 1]";
                    return false;
                }
            }
        } else if (clock.State is null && !WorldClocks.TryWholeTicks(clock.PeriodSeconds ?? 0d, out _)) {
            reason = $"clock '{clock.Name}' has no period of whole engine ticks";
            return false;
        }
        active.Remove(clock.Name);
        reason = string.Empty;
        return true;
    }
}
