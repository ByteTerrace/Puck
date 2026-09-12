namespace Puck.Transpiler.Units;

/// <summary>The physical dimension a unit suffix measures. A document's own vocabulary decides WHICH field carries
/// which dimension; this type only names the dimensions themselves and what they are worth, which is the same
/// wherever a number is authored.</summary>
public enum UnitDimension {
    /// <summary>No dimension — the value admits no unit suffix at all.</summary>
    None,
    /// <summary>Degrees-native: <c>deg</c> passes through unconverted.</summary>
    Degrees,
    /// <summary>Radians-native: <c>deg</c> converts to radians, <c>rad</c> passes through.</summary>
    Radians,
    /// <summary>Seconds-native: <c>s</c> passes through, <c>ms</c> divides by 1000.</summary>
    Seconds,
    /// <summary>Metres-native: <c>m</c> passes through, <c>cm</c> divides by 100, <c>mm</c> by 1000.</summary>
    Meters,
    /// <summary>Hertz-native: <c>hz</c> passes through.</summary>
    Hertz,
    /// <summary>A 0..1 fraction: <c>%</c>/<c>pct</c> divides by 100.</summary>
    Fraction,
}
/// <summary>What each <see cref="UnitDimension"/>'s suffixes are spelled and what they convert to — the one place
/// the language knows that a millisecond is a thousandth of a second. Kept apart from any field-name table so a
/// document vocabulary contributes only its own classification (which of ITS fields is a time) and never a second
/// copy of the arithmetic.</summary>
public static class UnitConversion {
    private static readonly string[] s_degreesUnits = ["deg"];
    private static readonly string[] s_radiansUnits = ["deg", "rad"];
    private static readonly string[] s_secondsUnits = ["s", "ms"];
    private static readonly string[] s_metersUnits = ["m", "cm", "mm"];
    private static readonly string[] s_hertzUnits = ["hz"];
    private static readonly string[] s_fractionUnits = ["%", "pct"];

    /// <summary>Returns the unit spellings <paramref name="dimension"/> accepts, for a diagnostic message.</summary>
    /// <param name="dimension">The dimension to describe.</param>
    /// <returns>The accepted spellings, or an empty array for <see cref="UnitDimension.None"/>.</returns>
    public static string[] AcceptedUnits(UnitDimension dimension) => dimension switch {
        UnitDimension.Degrees => s_degreesUnits,
        UnitDimension.Radians => s_radiansUnits,
        UnitDimension.Seconds => s_secondsUnits,
        UnitDimension.Meters => s_metersUnits,
        UnitDimension.Hertz => s_hertzUnits,
        UnitDimension.Fraction => s_fractionUnits,
        _ => [],
    };
    /// <summary>Converts a number carrying a unit suffix into <paramref name="dimension"/>'s native unit.</summary>
    /// <param name="dimension">The dimension governing the value.</param>
    /// <param name="numericValue">The authored number.</param>
    /// <param name="unit">The suffix as written; matched without regard to case.</param>
    /// <param name="converted">The converted value, or <paramref name="numericValue"/> unchanged when this returns
    /// <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="dimension"/> accepts <paramref name="unit"/>. A
    /// <see langword="false"/> covers both "no dimension" and "wrong unit for this dimension"; a caller telling
    /// those apart in its diagnostics checks <paramref name="dimension"/> itself.</returns>
    public static bool TryConvert(UnitDimension dimension, double numericValue, string unit, out double converted) {
        ArgumentNullException.ThrowIfNull(unit);

        var lowerUnit = unit.ToLowerInvariant();

        switch (dimension) {
            case UnitDimension.Degrees:
                if (lowerUnit == "deg") {
                    converted = numericValue;

                    return true;
                }
                break;

            case UnitDimension.Radians:
                if (lowerUnit == "deg") {
                    converted = Math.Round(value: ((numericValue * Math.PI) / 180.0), digits: 6);

                    return true;
                }
                if (lowerUnit == "rad") {
                    converted = numericValue;

                    return true;
                }
                break;

            case UnitDimension.Seconds:
                if (lowerUnit == "s") {
                    converted = numericValue;

                    return true;
                }
                if (lowerUnit == "ms") {
                    converted = (numericValue / 1000.0);

                    return true;
                }
                break;

            case UnitDimension.Meters:
                if (lowerUnit == "m") {
                    converted = numericValue;

                    return true;
                }
                if (lowerUnit == "cm") {
                    converted = (numericValue / 100.0);

                    return true;
                }
                if (lowerUnit == "mm") {
                    converted = (numericValue / 1000.0);

                    return true;
                }
                break;

            case UnitDimension.Hertz:
                if (lowerUnit == "hz") {
                    converted = numericValue;

                    return true;
                }
                break;

            case UnitDimension.Fraction:
                if (lowerUnit is "%" or "pct") {
                    converted = (numericValue / 100.0);

                    return true;
                }
                break;
        }

        converted = numericValue;

        return false;
    }
}
