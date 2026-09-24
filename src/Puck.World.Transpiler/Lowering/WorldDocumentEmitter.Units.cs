using Puck.Transpiler.Ast;
using Puck.Transpiler.Units;

namespace Puck.World.Transpiler.Lowering;

/// <summary>The field-dimension table: which <c>puck.world.definition.v1</c> field names carry which physical dimension.
/// The emitter converts against it and the decompiler classifies against it when deciding whether to print a unit
/// back onto a field's numeric value, so neither side carries its own copy. What each dimension's suffixes are
/// worth is <see cref="UnitConversion"/>'s — this table only says which of THIS document's fields is a length, a
/// time, an angle.</summary>
/// <remarks>A call argument is classified by its qualified <c>call.argument</c> key, an ordinary block property by
/// its bare name. Two fields can therefore share a bare name without sharing a dimension — <c>orbit(yaw:)</c> is
/// radians, a <c>yaw:</c> property on a pose row is not, and only the qualified form converts.</remarks>
public static class WorldDocumentEmitterUnits {
    private static readonly HashSet<string> DegreesNativeFields = new(comparer: StringComparer.Ordinal) {
        "yawDegrees", "localYawDegrees", "outwardYawDegrees", "outwardPitchDegrees",
    };
    // Qualified `call.argument` keys only: `pitch`/`yaw` are radians as `orbit` arguments and degrees-or-anything
    // as a bare property elsewhere, so a bare name must never reach the radians conversion.
    private static readonly HashSet<string> RadiansCallArguments = new(comparer: StringComparer.Ordinal) {
        "orbit.pitch", "orbit.yaw",
    };
    private static readonly HashSet<string> MetersFields = new(comparer: StringComparer.Ordinal) {
        "position", "scale", "radius", "margin", "reach", "standoff", "cellSize", "spacing", "distance",
    };
    private static readonly HashSet<string> FractionFields = new(comparer: StringComparer.Ordinal) {
        "alpha", "opacity", "fraction", "ratio", "percent", "chance", "probability",
    };

    /// <summary>Classifies a field name into the dimension whose accepted units and conversion govern it.</summary>
    /// <param name="fieldKey">The bare property name, or the qualified <c>call.argument</c> key.</param>
    /// <returns>The field's dimension, or <see cref="UnitDimension.None"/> when the field is absent from the table
    /// — a unit suffix on one of those is PUCK024.</returns>
    public static UnitDimension Classify(string fieldKey) {
        ArgumentNullException.ThrowIfNull(fieldKey);

        if (RadiansCallArguments.Contains(item: fieldKey)) {
            return UnitDimension.Radians;
        }

        var bare = QualifiedName.Parse(text: fieldKey).Last;

        if (
            DegreesNativeFields.Contains(item: bare) ||
            bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "YawDegrees"
        ) ||
            bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "PitchDegrees"
        )
        ) {
            return UnitDimension.Degrees;
        }
        if (bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Radians"
        )) {
            return UnitDimension.Radians;
        }
        if (bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Seconds"
        )) {
            return UnitDimension.Seconds;
        }
        if (
            MetersFields.Contains(item: bare) ||
            bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Meters"
        )
        ) {
            return UnitDimension.Meters;
        }
        if (bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Hertz"
        )) {
            return UnitDimension.Hertz;
        }
        if (
            FractionFields.Contains(item: bare) ||
            bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Fraction"
        ) ||
            bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Ratio"
        ) ||
            bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Percent"
        )
        ) {
            return UnitDimension.Fraction;
        }

        return UnitDimension.None;
    }
}
