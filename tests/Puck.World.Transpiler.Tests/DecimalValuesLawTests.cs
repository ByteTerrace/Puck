using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: a decimal-valued field never takes its value from a cast of a double. A literal
/// answers with its authored text, exactly and however many digits it spells; a computed double answers with its
/// fifteen significant digits, so the binary tail of a value such as 0.12 never reaches a document. Both are fixed
/// functions of their operand, which a cast is not across runtimes.</summary>
public sealed class DecimalValuesLawTests {
    private static LiteralExpressionNode Literal(object value, string? text) => new(
        Column: 1,
        Length: (text?.Length ?? 1),
        Line: 1,
        Offset: 0,
        RawText: text,
        Unit: null,
        Value: value
    );

    [InlineData(0.12, "0.12")]
    [InlineData(0.05, "0.05")]
    [InlineData(0.1 + 0.2, "0.3")]
    [InlineData(1.1 * 1.1, "1.21")]
    [InlineData(1e-7, "0.0000001")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(0d, "0")]
    [Theory]
    public void AComputedDoubleAnswersWithItsFifteenSignificantDigits(double value, string expected) => Assert.Equal(
        actual: DecimalValues.FromDouble(value: value),
        expected: decimal.Parse(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            s: expected
        )
    );
    [InlineData("0.12")]
    [InlineData("0.1234567890123456789012345")]
    [InlineData("1.5e3")]
    [InlineData("-0.000000000000000001")]
    [Theory]
    public void ALiteralAnswersWithItsAuthoredTextExactly(string text) => Assert.Equal(
        actual: DecimalValues.FromLiteral(literal: Literal(
            text: text,
            value: double.Parse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                s: text
            )
        )),
        expected: decimal.Parse(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            s: text,
            style: System.Globalization.NumberStyles.Float
        )
    );
    [Fact]
    public void AnIntegerLiteralAnswersWithItsValue() => Assert.Equal(
        actual: DecimalValues.FromLiteral(literal: Literal(
            text: null,
            value: 42L
        )),
        expected: 42m
    );
}
