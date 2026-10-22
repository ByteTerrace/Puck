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
        "orbit.pitch", "orbit.yaw", "sunDisc.radius", "clouds.curl", "aurora.base", "aurora.height", "aurora.fold",
        "spot.angle", "spot.innerAngle",
    };
    // The lights' photometric quantities, each a field whose number is in its one unit. `power` and `intensity` name
    // other quantities elsewhere (a rim's exponent, a sun disc's brightness), so only a point's and a spot's qualify.
    private static readonly Dictionary<string, UnitDimension> PhotometricCallArguments = new(comparer: StringComparer.Ordinal) {
        ["point.power"] = UnitDimension.Lumens,
        ["spot.power"] = UnitDimension.Lumens,
        ["point.intensity"] = UnitDimension.Candela,
        ["spot.intensity"] = UnitDimension.Candela,
    };
    private static readonly Dictionary<string, UnitDimension> PhotometricFields = new(comparer: StringComparer.Ordinal) {
        ["illuminance"] = UnitDimension.Lux,
        ["luminance"] = UnitDimension.Nits,
        ["emissive"] = UnitDimension.Nits,
        ["temperature"] = UnitDimension.Kelvin,
    };
    // An angle a light or the sky keeps in radians under a name that does not say so, written in degrees.
    private static readonly HashSet<string> RadiansFields = new(comparer: StringComparer.Ordinal) {
        "angularRadius", "band", "feather", "spread", "turn", "tilt",
    };
    private static readonly HashSet<string> MetersFields = new(comparer: StringComparer.Ordinal) {
        "position", "scale", "radius", "margin", "reach", "standoff", "cellSize", "spacing", "distance",
    };
    // A key's time on its clock: seconds, minutes or hours on a clock whose span reads as a time of day.
    private static readonly HashSet<string> SecondsFields = new(comparer: StringComparer.Ordinal) {
        "at",
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
        if (PhotometricCallArguments.TryGetValue(key: fieldKey, value: out var photometric)) {
            return photometric;
        }

        var bare = QualifiedName.Parse(text: fieldKey).Last;

        if (PhotometricFields.TryGetValue(key: bare, value: out var quantity)) {
            return quantity;
        }

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
        if (
            RadiansFields.Contains(item: bare) ||
            bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Radians"
        )
        ) {
            return UnitDimension.Radians;
        }
        if (
            SecondsFields.Contains(item: bare) ||
            bare.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Seconds"
        )
        ) {
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
