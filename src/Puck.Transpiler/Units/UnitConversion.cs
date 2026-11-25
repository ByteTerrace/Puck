using System.Globalization;
using System.Text;

namespace Puck.Transpiler.Units;

/// <summary>The physical dimension a document field is classified as: which units a value placed in it may carry and
/// which unit its number is written in. A document's own vocabulary decides WHICH field carries which dimension; this
/// type only names the classes, and <see cref="UnitConversion.DimensionOf(UnitDimension)"/> says what each measures.</summary>
public enum UnitDimension {
    /// <summary>No dimension: a value placed in the field carries no unit.</summary>
    None,
    /// <summary>An angle written in degrees.</summary>
    Degrees,
    /// <summary>An angle written in radians.</summary>
    Radians,
    /// <summary>A time written in seconds.</summary>
    Seconds,
    /// <summary>A length written in metres.</summary>
    Meters,
    /// <summary>A frequency written in hertz.</summary>
    Hertz,
    /// <summary>A 0..1 fraction; <c>%</c> and <c>pct</c> divide by 100.</summary>
    Fraction,
    /// <summary>Illuminance written in lux.</summary>
    Lux,
    /// <summary>Luminous power written in lumens.</summary>
    Lumens,
    /// <summary>Luminous intensity written in candela.</summary>
    Candela,
    /// <summary>Luminance written in nits (candela per square metre).</summary>
    Nits,
    /// <summary>Colour temperature written in kelvin.</summary>
    Kelvin,
}
/// <summary>A physical dimension as integer powers of the base quantities a document measures: length, time, angle,
/// luminous power, luminous intensity and temperature. Dimensions multiply by adding powers, so a speed is length over
/// time and an illuminance luminous power over length squared. The default value is dimensionless.</summary>
/// <param name="Length">The power of length (metres).</param>
/// <param name="Time">The power of time (seconds).</param>
/// <param name="Angle">The power of angle (radians).</param>
/// <param name="Flux">The power of luminous power (lumens).</param>
/// <param name="Intensity">The power of luminous intensity (candela).</param>
/// <param name="Temperature">The power of temperature (kelvin).</param>
public readonly record struct PhysicalDimension(sbyte Length = 0, sbyte Time = 0, sbyte Angle = 0, sbyte Flux = 0, sbyte Intensity = 0, sbyte Temperature = 0) {
    /// <summary>Gets the dimensionless dimension, of a pure number.</summary>
    public static PhysicalDimension None => default;
    /// <summary>Gets whether the dimension is dimensionless.</summary>
    public bool IsNone => (this == default);

    /// <summary>Returns the dimension of a product.</summary>
    /// <param name="left">The first factor's dimension.</param>
    /// <param name="right">The second factor's dimension.</param>
    /// <returns>The product's dimension.</returns>
    public static PhysicalDimension operator *(PhysicalDimension left, PhysicalDimension right) => new(
        Angle: ((sbyte)(left.Angle + right.Angle)),
        Flux: ((sbyte)(left.Flux + right.Flux)),
        Intensity: ((sbyte)(left.Intensity + right.Intensity)),
        Length: ((sbyte)(left.Length + right.Length)),
        Temperature: ((sbyte)(left.Temperature + right.Temperature)),
        Time: ((sbyte)(left.Time + right.Time))
    );
    /// <summary>Returns the dimension of a quotient.</summary>
    /// <param name="left">The dividend's dimension.</param>
    /// <param name="right">The divisor's dimension.</param>
    /// <returns>The quotient's dimension.</returns>
    public static PhysicalDimension operator /(PhysicalDimension left, PhysicalDimension right) => (left * right.Power(exponent: -1));

    /// <summary>Returns the dimension raised to a whole power.</summary>
    /// <param name="exponent">The power.</param>
    /// <returns>The raised dimension.</returns>
    public PhysicalDimension Power(int exponent) => new(
        Angle: ((sbyte)(Angle * exponent)),
        Flux: ((sbyte)(Flux * exponent)),
        Intensity: ((sbyte)(Intensity * exponent)),
        Length: ((sbyte)(Length * exponent)),
        Temperature: ((sbyte)(Temperature * exponent)),
        Time: ((sbyte)(Time * exponent))
    );
    /// <summary>Returns the dimension's square root, when every power is even.</summary>
    /// <param name="root">The root, on success.</param>
    /// <returns><see langword="true"/> when every power is even.</returns>
    public bool TrySquareRoot(out PhysicalDimension root) {
        root = new PhysicalDimension(
            Angle: ((sbyte)(Angle / 2)),
            Flux: ((sbyte)(Flux / 2)),
            Intensity: ((sbyte)(Intensity / 2)),
            Length: ((sbyte)(Length / 2)),
            Temperature: ((sbyte)(Temperature / 2)),
            Time: ((sbyte)(Time / 2))
        );

        return (root.Power(exponent: 2) == this);
    }
    /// <summary>Returns the dimension spelled in the canonical units, positive powers over negative:
    /// <c>m/s^2</c>, <c>lm/m^2</c>, or <c>a pure number</c>.</summary>
    /// <returns>The spelling.</returns>
    public override string ToString() {
        if (IsNone) {
            return "a pure number";
        }

        var over = new StringBuilder();
        var under = new StringBuilder();

        Append(over: over, power: Length, symbol: "m", under: under);
        Append(over: over, power: Time, symbol: "s", under: under);
        Append(over: over, power: Angle, symbol: "rad", under: under);
        Append(over: over, power: Flux, symbol: "lm", under: under);
        Append(over: over, power: Intensity, symbol: "cd", under: under);
        Append(over: over, power: Temperature, symbol: "K", under: under);

        var numerator = ((over.Length == 0) ? "1" : over.ToString());

        return ((under.Length == 0) ? numerator : $"{numerator}/{under}");
    }
    private static void Append(StringBuilder over, StringBuilder under, sbyte power, string symbol) {
        if (power == 0) {
            return;
        }

        var target = ((power > 0) ? over : under);
        var magnitude = Math.Abs(value: power);

        if (target.Length > 0) {
            target.Append(value: '*');
        }

        target.Append(value: symbol);

        if (magnitude != 1) {
            target.Append(provider: CultureInfo.InvariantCulture, handler: $"^{magnitude}");
        }
    }
}
/// <summary>A unit an author wrote after a number or in an annotation: what it measures and what one of it is worth in
/// the canonical units (metres, seconds, radians or degrees, lumens, candela, kelvin).</summary>
/// <param name="Dimension">What the unit measures.</param>
/// <param name="Exact">One of the unit in the canonical units as an exact decimal, or <see langword="null"/> when no
/// decimal holds it (a degree in radians).</param>
/// <param name="Factor">One of the unit in the canonical units.</param>
public readonly record struct UnitValue(PhysicalDimension Dimension, decimal? Exact, double Factor);
/// <summary>The language's units: what each suffix measures and what it is worth, and how a compound unit (<c>m/s</c>,
/// <c>m/s^2</c>, <c>lm/m^2</c>) is read from them. The one place the language knows that a millisecond is a thousandth
/// of a second. Kept apart from any field table, so a document vocabulary contributes only its own classification
/// (which of ITS fields is a time) and never a second copy of the arithmetic.</summary>
/// <remarks>Every canonical unit is the unit its field is written in, except an angle: a field classified
/// <see cref="UnitDimension.Degrees"/> holds degrees and every other angle holds radians, so the worth of <c>deg</c>
/// and <c>rad</c> depends on which an angle is written in.</remarks>
public static class UnitConversion {
    private static readonly PhysicalDimension Length = new(Length: 1);
    private static readonly PhysicalDimension Time = new(Time: 1);
    private static readonly PhysicalDimension Angle = new(Angle: 1);
    private static readonly PhysicalDimension Flux = new(Flux: 1);
    private static readonly PhysicalDimension Intensity = new(Intensity: 1);
    private static readonly PhysicalDimension Temperature = new(Temperature: 1);

    // Each suffix, matched without regard to case, with what it measures and its worth in the canonical unit; an angle's
    // worth is the radians one.
    private static readonly Dictionary<string, (PhysicalDimension Dimension, decimal Worth)> Atoms = new(comparer: StringComparer.OrdinalIgnoreCase) {
        ["m"] = (Length, 1m),
        ["cm"] = (Length, 0.01m),
        ["mm"] = (Length, 0.001m),
        ["s"] = (Time, 1m),
        ["ms"] = (Time, 0.001m),
        ["min"] = (Time, 60m),
        ["h"] = (Time, 3600m),
        ["hz"] = (Time.Power(exponent: -1), 1m),
        ["rad"] = (Angle, 1m),
        ["deg"] = (Angle, 0m),
        ["%"] = (PhysicalDimension.None, 0.01m),
        ["pct"] = (PhysicalDimension.None, 0.01m),
        ["lx"] = (Flux / Length.Power(exponent: 2), 1m),
        ["lm"] = (Flux, 1m),
        ["cd"] = (Intensity, 1m),
        ["nit"] = (Intensity / Length.Power(exponent: 2), 1m),
        ["nits"] = (Intensity / Length.Power(exponent: 2), 1m),
        ["K"] = (Temperature, 1m),
    };

    /// <summary>Returns what a field class measures.</summary>
    /// <param name="dimension">The field's class.</param>
    /// <returns>Its physical dimension, or <see langword="null"/> for <see cref="UnitDimension.None"/>, a field that
    /// admits no unit.</returns>
    public static PhysicalDimension? DimensionOf(UnitDimension dimension) => (dimension switch {
        UnitDimension.Degrees or UnitDimension.Radians => Angle,
        UnitDimension.Seconds => Time,
        UnitDimension.Meters => Length,
        UnitDimension.Hertz => Time.Power(exponent: -1),
        UnitDimension.Fraction => PhysicalDimension.None,
        UnitDimension.Lux => (Flux / Length.Power(exponent: 2)),
        UnitDimension.Lumens => Flux,
        UnitDimension.Candela => Intensity,
        UnitDimension.Nits => (Intensity / Length.Power(exponent: 2)),
        UnitDimension.Kelvin => Temperature,
        _ => null,
    });
    /// <summary>Returns the unit spellings <paramref name="dimension"/> accepts on its own, for a diagnostic
    /// message.</summary>
    /// <param name="dimension">The dimension to describe.</param>
    /// <returns>The suffixes that measure it, or an empty array for <see cref="UnitDimension.None"/>.</returns>
    public static string[] AcceptedUnits(UnitDimension dimension) => ((DimensionOf(dimension: dimension) is { } measured)
        ? [.. Atoms.Where(predicate: atom => (atom.Value.Dimension == measured)).Select(selector: static atom => atom.Key)]
        : []);
    /// <summary>Reads a unit as written: one suffix, or suffixes joined by <c>/</c>, each optionally raised to a whole
    /// power with <c>^</c> (<c>m</c>, <c>m/s</c>, <c>m/s^2</c>, <c>m^2</c>).</summary>
    /// <param name="unit">The unit as written.</param>
    /// <param name="degrees">Whether an angle is written in degrees rather than radians.</param>
    /// <param name="value">What the unit measures and its worth, on success.</param>
    /// <returns><see langword="true"/> when every part of the unit is a known suffix with a whole power.</returns>
    public static bool TryRead(string unit, bool degrees, out UnitValue value) {
        ArgumentNullException.ThrowIfNull(unit);

        value = default;

        if (unit.Length == 0) {
            return false;
        }

        var dimension = PhysicalDimension.None;
        decimal? exact = 1m;
        var factor = 1d;
        var parts = unit.Split(separator: '/');

        for (var index = 0; (index < parts.Length); index++) {
            var part = parts[index];
            var power = 1;
            var caret = part.IndexOf(value: '^');

            if (caret >= 0) {
                if (!int.TryParse(s: part[(caret + 1)..], style: NumberStyles.AllowLeadingSign, provider: CultureInfo.InvariantCulture, result: out power) || (power == 0) || (Math.Abs(value: power) > 4)) {
                    return false;
                }

                part = part[..caret];
            }

            if (!Atoms.TryGetValue(key: part, value: out var atom)) {
                return false;
            }

            if (index > 0) {
                power = -power;
            }

            var (atomExact, atomFactor) = Worth(atom: atom, degrees: degrees, name: part);

            dimension *= atom.Dimension.Power(exponent: power);
            factor *= Math.Pow(x: atomFactor, y: power);
            exact = ((exact is { } running) && (atomExact is { } each))
                ? Raise(power: power, running: running, value: each)
                : null;
        }

        value = new UnitValue(Dimension: dimension, Exact: exact, Factor: factor);

        return true;
    }
    // One suffix's worth, an angle's in the unit angles are written in.
    private static (decimal? Exact, double Factor) Worth((PhysicalDimension Dimension, decimal Worth) atom, bool degrees, string name) {
        if (atom.Dimension != Angle) {
            return (atom.Worth, ((double)atom.Worth));
        }

        var isDegree = string.Equals(a: name, b: "deg", comparisonType: StringComparison.OrdinalIgnoreCase);

        return (isDegree == degrees)
            ? (1m, 1d)
            : (isDegree ? (null, (Math.PI / 180d)) : (null, (180d / Math.PI)));
    }
    // A running exact product times a worth raised to a whole power, or null when a decimal cannot hold it exactly.
    private static decimal? Raise(decimal running, decimal value, int power) {
        try {
            for (var step = 0; (step < Math.Abs(value: power)); step++) {
                if (power > 0) {
                    running *= value;
                } else {
                    var quotient = (running / value);

                    if ((quotient * value) != running) {
                        return null;
                    }

                    running = quotient;
                }
            }

            return running;
        } catch (OverflowException) {
            return null;
        }
    }
    /// <summary>Converts an exact number carrying a unit into <paramref name="dimension"/>'s own unit, when the unit
    /// measures that dimension and a decimal holds the converted value exactly.</summary>
    /// <param name="dimension">The field's class.</param>
    /// <param name="value">The authored number.</param>
    /// <param name="unit">The unit as written; its suffixes match without regard to case.</param>
    /// <param name="converted">The converted value.</param>
    /// <returns><see langword="true"/> when the unit measures <paramref name="dimension"/> and the conversion is exact.
    /// Degrees into radians returns <see langword="false"/>, and so does a value a division would round: both are
    /// <see cref="TryConvert"/>'s.</returns>
    public static bool TryConvertExact(UnitDimension dimension, decimal value, string unit, out decimal converted) {
        converted = value;

        return (
            (DimensionOf(dimension: dimension) is { } measured) &&
            TryRead(degrees: (dimension == UnitDimension.Degrees), unit: unit, value: out var read) &&
            (read.Dimension == measured) &&
            (read.Exact is { } worth) &&
            TryScaleExact(scaled: out converted, value: value, worth: worth)
        );
    }
    /// <summary>Multiplies an exact number by a unit's exact worth, when a decimal holds the product exactly.</summary>
    /// <param name="value">The number.</param>
    /// <param name="worth">The unit's worth in the canonical unit.</param>
    /// <param name="scaled">The product, or <paramref name="value"/> when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the product neither overflows nor rounds.</returns>
    public static bool TryScaleExact(decimal value, decimal worth, out decimal scaled) {
        scaled = value;

        if (Raise(power: 1, running: value, value: worth) is not { } product) {
            return false;
        }

        // A product by a worth below one may round where the division it stands for would not: dividing back through
        // the worth checks the value lost nothing.
        if ((worth < 1m) && (Raise(power: -1, running: product, value: worth) != value)) {
            return false;
        }

        scaled = product;

        return true;
    }
    /// <summary>Converts a number carrying a unit into <paramref name="dimension"/>'s own unit.</summary>
    /// <param name="dimension">The field's class.</param>
    /// <param name="numericValue">The authored number.</param>
    /// <param name="unit">The unit as written; its suffixes match without regard to case.</param>
    /// <param name="converted">The converted value, or <paramref name="numericValue"/> unchanged when this returns
    /// <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the unit measures <paramref name="dimension"/>.</returns>
    public static bool TryConvert(UnitDimension dimension, double numericValue, string unit, out double converted) {
        converted = numericValue;

        if (
            (DimensionOf(dimension: dimension) is not { } measured) ||
            !TryRead(degrees: (dimension == UnitDimension.Degrees), unit: unit, value: out var read) ||
            (read.Dimension != measured)
        ) {
            return false;
        }

        converted = Scale(crossesAngleUnits: (read.Exact is null), factor: read.Factor, value: numericValue);

        return true;
    }
    /// <summary>Returns a number times a unit's worth. A value whose angle crossed between degrees and radians rounds to
    /// six places, so a document holds the same decimal however often it is compiled.</summary>
    /// <param name="value">The number.</param>
    /// <param name="factor">The unit's worth.</param>
    /// <param name="crossesAngleUnits">Whether the worth crossed between degrees and radians, which no decimal holds
    /// exactly.</param>
    /// <returns>The scaled number.</returns>
    public static double Scale(double value, double factor, bool crossesAngleUnits) => (crossesAngleUnits
        ? Math.Round(digits: 6, value: (value * factor))
        : (value * factor)
    );
}
