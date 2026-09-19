using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: a decimal-valued field never takes its value from a cast of a double. A literal
/// answers with its authored text, exactly and however many digits it spells; a computed double answers with its
/// fifteen significant digits, so the binary tail of a value such as 0.12 never reaches a document. Both are fixed
/// functions of their operand, which a cast is not across runtimes. An authored decimal stays exact through the
/// lowering for as long as the arithmetic applied to it is closed over finite decimals: a sign, a sum, a difference,
/// a product, and a unit whose scale is a power of ten.</summary>
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
    // Each origin component is lowered through the general expression path, the one every field's value takes.
    private static string[] LoweredOrigin(string x, string y, string z) {
        var screens = Assert.IsType<JsonArray>(@object: WorldCompiler.Compile(source: $$"""
            schema: "puck.world.definition.v1"
            let long = 0.1234567890123456789012345
            screens [
                { origin [{{x}}, {{y}}, {{z}}] }
            ]
            """).RequireJson()["screens"]);
        var origin = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: screens[0])["origin"]);

        return [.. origin.Select(selector: static component => component!.ToJsonString())];
    }

    [Fact]
    public void AnAuthoredDecimalKeepsEveryDigitThroughTheLowering() => Assert.Equal(
        actual: LoweredOrigin(
            x: "0.1234567890123456789012345",
            y: "-long",
            z: "long"
        ),
        expected: [
            "0.1234567890123456789012345",
            "-0.1234567890123456789012345",
            "0.1234567890123456789012345",
        ]
    );
    [Fact]
    public void ExactArithmeticOverAuthoredDecimalsStaysExact() => Assert.Equal(
        actual: LoweredOrigin(
            x: "0.1 + 0.2",
            y: "long - 0.0234567890123456789012345",
            z: "1.1 * 1.1"
        ),
        expected: [
            "0.3",
            "0.1",
            "1.21",
        ]
    );
    [Fact]
    public void OneValueHasOneSpelling() => Assert.Equal(
        actual: LoweredOrigin(
            x: "1.50",
            y: "2.0",
            z: "1.5e3"
        ),
        expected: [
            "1.5",
            "2",
            "1500",
        ]
    );
    [Fact]
    public void AMagnitudeADecimalCannotHoldStaysTheDoubleItWas() => Assert.Equal(
        actual: LoweredOrigin(
            x: "1e-40",
            y: "1e40",
            z: "0.5"
        ),
        expected: [
            "1E-40",
            "1E+40",
            "0.5",
        ]
    );
    // A comparison is worth 1 or 0, and it is decided on the exact values, so it agrees with the exact arithmetic
    // beside it: a difference the subtraction keeps is a difference the comparison sees.
    [Fact]
    public void AComparisonIsDecidedOnTheExactValues() => Assert.Equal(
        actual: LoweredOrigin(
            x: "1.0000000000000001 > 1",
            y: "(1.0000000000000001 - 1) > 0",
            z: "0.1 + 0.2 == 0.3"
        ),
        expected: [
            "1",
            "1",
            "1",
        ]
    );
    [Fact]
    public void NumbersThatCompareEqualHashEqualHoweverEachIsHeld() {
        JsonNode?[] halves = [
            JsonValue.Create(value: 0.5),
            JsonValue.Create(value: 0.5m),
            JsonValue.Create(value: 0.50m),
        ];
        JsonNode?[] twos = [
            JsonValue.Create(value: 2L),
            JsonValue.Create(value: 2.0),
            JsonValue.Create(value: 2.0m),
            JsonValue.Create(value: 2),
        ];

        foreach (var group in new[] { halves, twos }) {
            foreach (var left in group) {
                foreach (var right in group) {
                    Assert.True(condition: DocumentValueEqualityComparer.Instance.Equals(
                        x: left,
                        y: right
                    ));
                    Assert.Equal(
                        actual: DocumentValueEqualityComparer.Instance.GetHashCode(obj: right),
                        expected: DocumentValueEqualityComparer.Instance.GetHashCode(obj: left)
                    );
                }
            }
        }

        // A tenth is not a binary fraction, so the double nearest it is a different number from the decimal.
        Assert.False(condition: DocumentValueEqualityComparer.Instance.Equals(
            x: JsonValue.Create(value: 0.1),
            y: JsonValue.Create(value: 0.1m)
        ));
    }
    // Decimal arithmetic rounds without saying so. A result a decimal would round keeps the magnitude the double
    // arithmetic gives it, in a product and in a unit conversion alike.
    [Fact]
    public void AResultADecimalWouldRoundIsComputedInDouble() {
        Assert.False(condition: DocumentNumbers.TryExactArithmetic(
            left: 1e-28m,
            operation: "*",
            result: out _,
            right: 0.1m
        ));
        Assert.False(condition: DocumentNumbers.TryExactArithmetic(
            left: decimal.MaxValue,
            operation: "+",
            result: out _,
            right: 0.5m
        ));
        Assert.True(condition: DocumentNumbers.TryExactArithmetic(
            left: 0.1m,
            operation: "+",
            result: out var sum,
            right: 0.2m
        ));
        Assert.Equal(
            actual: sum,
            expected: 0.3m
        );
        Assert.Equal(
            actual: LoweredOrigin(
                x: "0.0000000000000000000000000001 * 0.1",
                y: "0.5 * 0.5",
                z: "0"
            ),
            expected: [
                JsonValue.Create(value: (1e-28 * 0.1)).ToJsonString(),
                "0.25",
                "0",
            ]
        );
        Assert.False(condition: Puck.Transpiler.Units.UnitConversion.TryConvertExact(
            converted: out _,
            dimension: Puck.Transpiler.Units.UnitDimension.Seconds,
            unit: "ms",
            value: 1e-28m
        ));
        Assert.True(condition: Puck.Transpiler.Units.UnitConversion.TryConvertExact(
            converted: out var seconds,
            dimension: Puck.Transpiler.Units.UnitDimension.Seconds,
            unit: "ms",
            value: 250m
        ));
        Assert.Equal(
            actual: seconds,
            expected: 0.25m
        );
    }
    [Fact]
    public void AnIntegerLiteralAnswersWithItsValue() => Assert.Equal(
        actual: DecimalValues.FromLiteral(literal: Literal(
            text: null,
            value: 42L
        )),
        expected: 42m
    );
}
