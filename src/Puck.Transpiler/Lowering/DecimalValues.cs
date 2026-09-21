using System.Globalization;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Lowering;

/// <summary>Reads a decimal-valued field out of what the parser and the lowering carry, the same on every runtime.
/// </summary>
/// <remarks>
/// <para>A number literal is held as a <see cref="double"/>, and a cast from <see cref="double"/> to
/// <see cref="decimal"/> is not a fixed function of its operand: the runtime chooses how many digits of the binary
/// value the result keeps, and runtime versions choose differently. A field whose document spelling is
/// a decimal is therefore never cast. It is read from the literal's own text where there is one, and from the
/// double's fifteen significant digits where there is only a computed value.</para>
/// <para>An authored decimal stays a decimal for as long as the arithmetic applied to it is closed over finite
/// decimals: a sign, a sum, a difference, a product, and a unit whose scale is a power of ten. A quotient, a
/// remainder, a function and an angle conversion are computed in <see cref="double"/>, and what they yield is
/// read back through <see cref="FromDouble"/>.</para>
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
    /// <summary>Reads the decimal a number literal's own text spells.</summary>
    /// <param name="literal">The literal.</param>
    /// <param name="authored">The decimal, to the twenty-eight significant digits a <see cref="decimal"/>
    /// carries.</param>
    /// <returns><see langword="true"/> when the literal kept its text and a <see cref="decimal"/> holds it. A
    /// magnitude above the type's range, or one so small it would read as zero, returns
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="literal"/> is <see langword="null"/>.</exception>
    public static bool TryAuthored(LiteralExpressionNode literal, out decimal authored) {
        ArgumentNullException.ThrowIfNull(argument: literal);

        return (
            decimal.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out authored,
                s: literal.RawText,
                style: NumberStyles.Float
            ) &&
            (
                (authored != 0m) ||
                (literal.Value is not double real) ||
                (real == 0.0)
            )
        );
    }
}
