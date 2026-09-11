namespace Puck.World.Transpiler.Lowering;

/// <summary>The field-dimension table (§5 of the sugar wave): which JSON field names admit which unit suffixes, and
/// how each converts. Shared by the emitter (this project's own <c>WorldDocumentEmitter.LowerUnitLiteral</c>) and,
/// symmetrically, by the decompiler when it decides whether to print a unit back onto a field's numeric value —
/// both consult this one table rather than each carrying its own copy.</summary>
public static class WorldDocumentEmitterUnits {
    /// <summary>Which dimension a field belongs to, or <see cref="Unknown"/> when the field admits no unit at all.</summary>
    public enum FieldDimensionKind {
        /// <summary>The field is absent from the table — a unit suffix on it is PUCK024.</summary>
        Unknown,
        /// <summary>Degrees-native (`yawDegrees`, `outwardPitchDegrees`, …): `deg` passes through unconverted.</summary>
        DegreesNative,
        /// <summary>Radians-native (`orbit(pitch:, yaw:)`, any `…Radians` field): `deg` converts to radians, `rad` passes through.</summary>
        Radians,
        /// <summary>Any `…Seconds` field: `s` passes through, `ms` divides by 1000.</summary>
        Seconds,
        /// <summary>`position`/`scale`/`radius`/`margin`/`reach`/`standoff`/`cellSize`/`spacing`, or any `…Meters` field: `m` passes through, `cm` divides by 100, `mm` by 1000.</summary>
        Meters,
        /// <summary>Any `…Hertz` field: `hz` passes through.</summary>
        Hertz,
    }

    private static readonly string[] s_degreesUnits = ["deg"];
    private static readonly string[] s_radiansUnits = ["deg", "rad"];
    private static readonly string[] s_secondsUnits = ["s", "ms"];
    private static readonly string[] s_metersUnits = ["m", "cm", "mm"];
    private static readonly string[] s_hertzUnits = ["hz"];

    private static readonly HashSet<string> s_degreesNativeFields = new(StringComparer.Ordinal) {
        "yawDegrees", "localYawDegrees", "outwardYawDegrees", "outwardPitchDegrees",
    };

    private static readonly HashSet<string> s_radiansFields = new(StringComparer.Ordinal) {
        "pitch", "yaw",
    };

    private static readonly HashSet<string> s_metersFields = new(StringComparer.Ordinal) {
        "position", "scale", "radius", "margin", "reach", "standoff", "cellSize", "spacing",
    };

    /// <summary>Classifies a field name into the dimension whose accepted units and conversion govern it.</summary>
    public static FieldDimensionKind Classify(string fieldKey) {
        if (s_degreesNativeFields.Contains(fieldKey) || fieldKey.EndsWith("YawDegrees", StringComparison.Ordinal) || fieldKey.EndsWith("PitchDegrees", StringComparison.Ordinal)) {
            return FieldDimensionKind.DegreesNative;
        }
        if (s_radiansFields.Contains(fieldKey) || fieldKey.EndsWith("Radians", StringComparison.Ordinal)) {
            return FieldDimensionKind.Radians;
        }
        if (fieldKey.EndsWith("Seconds", StringComparison.Ordinal)) {
            return FieldDimensionKind.Seconds;
        }
        if (s_metersFields.Contains(fieldKey) || fieldKey.EndsWith("Meters", StringComparison.Ordinal)) {
            return FieldDimensionKind.Meters;
        }
        if (fieldKey.EndsWith("Hertz", StringComparison.Ordinal)) {
            return FieldDimensionKind.Hertz;
        }
        return FieldDimensionKind.Unknown;
    }

    /// <summary>Returns the unit spellings <paramref name="kind"/> accepts, for a diagnostic message.</summary>
    public static string[] AcceptedUnitsFor(FieldDimensionKind kind) => kind switch {
        FieldDimensionKind.DegreesNative => s_degreesUnits,
        FieldDimensionKind.Radians => s_radiansUnits,
        FieldDimensionKind.Seconds => s_secondsUnits,
        FieldDimensionKind.Meters => s_metersUnits,
        FieldDimensionKind.Hertz => s_hertzUnits,
        _ => [],
    };

    /// <summary>Converts <paramref name="numericValue"/> carrying <paramref name="unit"/> against
    /// <paramref name="fieldKey"/>'s dimension. Returns <see langword="false"/> when the field admits no unit, or
    /// admits units but not this one — the caller distinguishes those two refusals (PUCK024 vs PUCK025) by calling
    /// <see cref="Classify"/> itself.</summary>
    public static bool TryConvert(string fieldKey, double numericValue, string unit, out double converted) {
        var lowerUnit = unit.ToLowerInvariant();
        switch (Classify(fieldKey)) {
            case FieldDimensionKind.DegreesNative:
                if (lowerUnit == "deg") {
                    converted = numericValue;
                    return true;
                }
                break;

            case FieldDimensionKind.Radians:
                if (lowerUnit == "deg") {
                    converted = Math.Round(numericValue * Math.PI / 180.0, 6);
                    return true;
                }
                if (lowerUnit == "rad") {
                    converted = numericValue;
                    return true;
                }
                break;

            case FieldDimensionKind.Seconds:
                if (lowerUnit == "s") {
                    converted = numericValue;
                    return true;
                }
                if (lowerUnit == "ms") {
                    converted = numericValue / 1000.0;
                    return true;
                }
                break;

            case FieldDimensionKind.Meters:
                if (lowerUnit == "m") {
                    converted = numericValue;
                    return true;
                }
                if (lowerUnit == "cm") {
                    converted = numericValue / 100.0;
                    return true;
                }
                if (lowerUnit == "mm") {
                    converted = numericValue / 1000.0;
                    return true;
                }
                break;

            case FieldDimensionKind.Hertz:
                if (lowerUnit == "hz") {
                    converted = numericValue;
                    return true;
                }
                break;
        }

        converted = numericValue;
        return false;
    }
}
