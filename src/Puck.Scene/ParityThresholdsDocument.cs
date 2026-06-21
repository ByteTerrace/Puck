using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// Overrides for the cross-backend parity PASS thresholds (the <c>world</c> validation gate). Every field is optional;
/// an omitted field keeps the built-in calibrated default. A check is disabled by its documented sentinel
/// (<see cref="MaxChannelDelta"/> at 255, <see cref="MinUnitDeltaFraction"/>/<see cref="MinIsolatedFraction"/> at 0).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ParityThresholdsDocument {
    /// <summary>The largest allowed single-channel delta (0..255; 255 disables the check).</summary>
    public int? MaxChannelDelta { get; init; }
    /// <summary>The largest allowed differing-pixel percentage (0..100).</summary>
    public double? MaxPercentDiffering { get; init; }
    /// <summary>The smallest allowed fraction of differing pixels whose delta is exactly 1 (0..1; 0 disables).</summary>
    public double? MinUnitDeltaFraction { get; init; }
    /// <summary>The smallest allowed spatially-isolated fraction (0..1; 0 disables).</summary>
    public double? MinIsolatedFraction { get; init; }
    /// <summary>The largest allowed mean max-channel delta over all pixels (>= 0).</summary>
    public double? MaxMeanAbsError { get; init; }

    internal void Validate(string path, ValidationErrors errors) {
        RequireRange(errors: errors, max: 255, min: 0, name: "maxChannelDelta", path: $"{path}.maxChannelDelta", value: MaxChannelDelta);
        RequireRange(errors: errors, max: 100, min: 0, name: "maxPercentDiffering", path: $"{path}.maxPercentDiffering", value: MaxPercentDiffering);
        RequireRange(errors: errors, max: 1, min: 0, name: "minUnitDeltaFraction", path: $"{path}.minUnitDeltaFraction", value: MinUnitDeltaFraction);
        RequireRange(errors: errors, max: 1, min: 0, name: "minIsolatedFraction", path: $"{path}.minIsolatedFraction", value: MinIsolatedFraction);
        RequireRange(errors: errors, max: double.MaxValue, min: 0, name: "maxMeanAbsError", path: $"{path}.maxMeanAbsError", value: MaxMeanAbsError);
    }

    private static void RequireRange(string path, string name, double? value, double min, double max, ValidationErrors errors) {
        if ((value is double actual) && (!double.IsFinite(d: actual) || (actual < min) || (actual > max))) {
            errors.Add(path: path, message: $"{name} {actual} must be in [{min}, {max}]");
        }
    }
}
