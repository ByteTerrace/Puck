using System.Globalization;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Lowering;

/// <summary>Reads a decimal-valued field out of what the parser and the lowering carry, the same on every runtime.
/// </summary>
/// <remarks>
/// <para>A number literal is held as a <see cref="double"/>, and a cast from <see cref="double"/> to
/// <see cref="decimal"/> is not a fixed function of its operand: the runtime chooses how many digits of the binary
/// value the result keeps, and runtime versions choose differently. A field whose document spelling is
/// a decimal is therefore never cast. It is read from the literal's own text where the literal spells at most
/// <see cref="ExactLiteralDigits"/> significant digits, and from the double's fifteen significant digits where there
/// is only a computed value or a literal of more digits (a double's round-trip spelling, which is the double).</para>
/// <para>An authored decimal stays a decimal for as long as the arithmetic applied to it is closed over finite
/// decimals: a sign, a sum, a difference, a product, a quotient whose exact value is a finite decimal, and a unit
/// whose scale is a power of ten. Any other quotient, a remainder, a function and an angle conversion are computed in
/// <see cref="double"/>, and what they yield is read back through <see cref="FromDouble"/>.</para>
/// </remarks>
public static class DecimalValues {
    /// <summary>Returns the decimal a computed double stands for: its value to fifteen significant digits, which
    /// is every digit a double is guaranteed to carry and none of its binary tail.</summary>
    /// <param name="value">The double.</param>
    /// <returns>The decimal.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> is not finite or lies outside the range of
    /// <see cref="decimal"/>.</exception>
    public static decimal FromDouble(double value) => decimal.Parse(
        provider: CultureInfo.InvariantCulture,
        s: value.ToString(
            format: "G15",
            provider: CultureInfo.InvariantCulture
        ),
        style: NumberStyles.Float
    );
    /// <summary>Returns the decimal a number literal spells: its authored text when it has a fraction or an
    /// exponent, exactly, and otherwise its integer value.</summary>
    /// <param name="literal">The literal.</param>
    /// <returns>The decimal, or zero for a literal that carries no number.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="literal"/> is <see langword="null"/>.</exception>
    public static decimal FromLiteral(LiteralExpressionNode literal) {
        ArgumentNullException.ThrowIfNull(argument: literal);

        if (TryAuthored(
            authored: out var authored,
            literal: literal
        )) {
            return authored;
        }

        return (literal.Value switch {
            decimal exact => exact,
            long integer => integer,
            ulong unsigned => unsigned,
            int narrow => narrow,
            double real => FromDouble(value: real),
            _ => 0m,
        });
    }
    /// <summary>The most significant digits an authored decimal literal may spell and stay an exact decimal: fifteen,
    /// the count every decimal of which survives a round trip through a <see cref="double"/> (<c>DBL_DIG</c>, from the
    /// 53-bit significand). A literal of more digits is a double's own round-trip spelling (a double needs sixteen or
    /// seventeen to be told apart from its neighbours), not a decimal an author chose, so it is the double it
    /// spells.</summary>
    public const int ExactLiteralDigits = 15;

    /// <summary>Reads the decimal a number literal's own text spells.</summary>
    /// <param name="literal">The literal.</param>
    /// <param name="authored">The decimal.</param>
    /// <returns><see langword="true"/> when the literal kept its text, spells at most
    /// <see cref="ExactLiteralDigits"/> significant digits and a <see cref="decimal"/> holds every digit of it. A
    /// literal of sixteen or more significant digits, a magnitude above the type's range and one so small it would
    /// read as zero return <see langword="false"/>, and the literal stays the <see cref="double"/> it parsed to: the
    /// same double a computed quotient with those digits is, so a pasted spelling of a double and the expression it came
    /// from are one value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="literal"/> is <see langword="null"/>.</exception>
    public static bool TryAuthored(LiteralExpressionNode literal, out decimal authored) {
        ArgumentNullException.ThrowIfNull(argument: literal);

        return (
            TryParseEvery(
                number: out authored,
                text: literal.RawText
            ) &&
            (SignificantDigits(text: literal.RawText!).Length <= ExactLiteralDigits)
        );
    }
    /// <summary>Reads the decimal a number's text spells, when a <see cref="decimal"/> holds every digit of it.</summary>
    /// <param name="text">The number's text: a literal as written, or a double's or a float's round-trip spelling.</param>
    /// <param name="number">The decimal, on success.</param>
    /// <returns><see langword="false"/> for text that is no number, a magnitude above the type's range, and one whose
    /// digits a <see cref="decimal"/> would round away, a magnitude so small it would read as zero included.</returns>
    public static bool TryParseEvery(string? text, out decimal number) => (
        decimal.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out number,
            s: text,
            style: NumberStyles.Float
        ) &&
        (SignificantDigits(text: text!) == SignificantDigits(text: number.ToString(provider: CultureInfo.InvariantCulture)))
    );
    // The digits a number spells, without its sign, point, exponent, or the zeros that only place them.
    private static string SignificantDigits(string text) {
        var exponent = text.AsSpan().IndexOfAny(value0: 'e', value1: 'E');
        var mantissa = ((exponent < 0) ? text : text[..exponent]);

        return new string(value: [.. mantissa.Where(predicate: char.IsAsciiDigit)]).Trim(trimChar: '0');
    }
}
