namespace Puck.World.Transpiler.Lowering;

/// <summary>The field-dimension table: which JSON field names admit which unit suffixes, and how each converts.
/// The emitter converts against it and the decompiler classifies against it when deciding whether to print a unit
/// back onto a field's numeric value, so neither side carries its own copy.</summary>
/// <remarks>A call argument is classified by its qualified <c>call.argument</c> key, an ordinary block property by
/// its bare name. Two fields can therefore share a bare name without sharing a dimension — <c>orbit(yaw:)</c> is
/// radians, a <c>yaw:</c> property on a pose row is not, and only the qualified form converts.</remarks>
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
        /// <summary>A field authored as a 0..1 fraction: `%`/`pct` divides by 100.</summary>
        Fraction,
    }

    private static readonly string[] s_degreesUnits = ["deg"];
    private static readonly string[] s_radiansUnits = ["deg", "rad"];
    private static readonly string[] s_secondsUnits = ["s", "ms"];
    private static readonly string[] s_metersUnits = ["m", "cm", "mm"];
    private static readonly string[] s_hertzUnits = ["hz"];
    private static readonly string[] s_fractionUnits = ["%", "pct"];

    private static readonly HashSet<string> s_degreesNativeFields = new(StringComparer.Ordinal) {
        "yawDegrees", "localYawDegrees", "outwardYawDegrees", "outwardPitchDegrees",
    };

    // Qualified `call.argument` keys only: `pitch`/`yaw` are radians as `orbit` arguments and degrees-or-anything
    // as a bare property elsewhere, so a bare name must never reach the radians conversion.
    private static readonly HashSet<string> s_radiansCallArguments = new(StringComparer.Ordinal) {
        "orbit.pitch", "orbit.yaw",
    };

    private static readonly HashSet<string> s_metersFields = new(StringComparer.Ordinal) {
        "position", "scale", "radius", "margin", "reach", "standoff", "cellSize", "spacing", "distance",
    };

    private static readonly HashSet<string> s_fractionFields = new(StringComparer.Ordinal) {
        "alpha", "opacity", "fraction", "ratio", "percent", "chance", "probability",
    };

    /// <summary>Classifies a field name into the dimension whose accepted units and conversion govern it.</summary>
    public static FieldDimensionKind Classify(string fieldKey) {
        ArgumentNullException.ThrowIfNull(fieldKey);

        if (s_radiansCallArguments.Contains(fieldKey)) {
            return FieldDimensionKind.Radians;
        }

        var separator = fieldKey.LastIndexOf('.');
        var bare = (separator >= 0) ? fieldKey[(separator + 1)..] : fieldKey;

        if (s_degreesNativeFields.Contains(bare) || bare.EndsWith("YawDegrees", StringComparison.Ordinal) || bare.EndsWith("PitchDegrees", StringComparison.Ordinal)) {
            return FieldDimensionKind.DegreesNative;
        }
        if (bare.EndsWith("Radians", StringComparison.Ordinal)) {
            return FieldDimensionKind.Radians;
        }
        if (bare.EndsWith("Seconds", StringComparison.Ordinal)) {
            return FieldDimensionKind.Seconds;
        }
        if (s_metersFields.Contains(bare) || bare.EndsWith("Meters", StringComparison.Ordinal)) {
            return FieldDimensionKind.Meters;
        }
        if (bare.EndsWith("Hertz", StringComparison.Ordinal)) {
            return FieldDimensionKind.Hertz;
        }
        if (s_fractionFields.Contains(bare) || bare.EndsWith("Fraction", StringComparison.Ordinal) || bare.EndsWith("Ratio", StringComparison.Ordinal) || bare.EndsWith("Percent", StringComparison.Ordinal)) {
            return FieldDimensionKind.Fraction;
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
        FieldDimensionKind.Fraction => s_fractionUnits,
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

            case FieldDimensionKind.Fraction:
                if (lowerUnit is "%" or "pct") {
                    converted = numericValue / 100.0;
                    return true;
                }
                break;
        }

        converted = numericValue;
        return false;
    }
}
