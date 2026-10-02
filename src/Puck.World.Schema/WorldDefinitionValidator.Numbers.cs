namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void RequireFinite(float value, string name, List<string> errors) {
        if (!float.IsFinite(f: value)) {
            errors.Add(item: $"{name} must be finite.");
        }
    }
    private static void RequireIntRange(int value, int min, int max, string name, List<string> errors) {
        if (
            (value < min) ||
            (value > max)
        ) {
            errors.Add(item: $"{name} {value} is outside {min}..{max}.");
        }
    }
    private static void RequireNonNegative(float value, string name, List<string> errors) {
        if (
            !float.IsFinite(f: value) ||
            (value < 0f)
        ) {
            errors.Add(item: $"{name} must be finite and non-negative.");
        }
    }
    private static void RequireNonNegativeEpoch(long value, string name, List<string> errors) {
        if (value < 0) {
            errors.Add(item: $"{name} {value} must be non-negative.");
        }
    }
    private static void RequirePositive(float value, string name, List<string> errors) {
        if (
            !float.IsFinite(f: value) ||
            (value <= 0f)
        ) {
            errors.Add(item: $"{name} must be finite and positive.");
        }
    }
    private static void RequireUnitInterval(float value, string name, List<string> errors) {
        if (
            !float.IsFinite(f: value) ||
            (value < 0f) ||
            (value > 1f)
        ) {
            errors.Add(item: $"{name} {value} must be within 0..1.");
        }
    }
    // The general bounded-float door every closed-interval check (unit alphas, gain ceilings, half-angle cones, …)
    // folds onto: finite, and within the interval WorldValueDomain spells, either edge switchable to an open bound (e.g.
    // a half-angle's 0 is excluded — a zero-width cone senses nothing — while its 180 ceiling is admitted).
    private static void RequireRange(float value, float min, float max, string name, List<string> errors, bool minExclusive = false, bool maxExclusive = false) {
        var domain = new WorldValueDomain(
            Maximum: max,
            MaximumOpen: maxExclusive,
            Minimum: min,
            MinimumOpen: minExclusive
        );

        if (!domain.Contains(value: value)) {
            errors.Add(item: $"{name} {value} must be finite and within {domain}.");
        }
    }
}
