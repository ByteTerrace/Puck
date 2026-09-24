using System.Text.Json.Nodes;
using Puck.State;
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
    [InlineData((0.1 + 0.2), "0.3")]
    [InlineData((1.1 * 1.1), "1.21")]
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
        var screens = Assert.IsType<JsonArray>(@object: WorldSources.LowerSourceClean(source: $$"""
            schema: "puck.world.definition.v1"
            let long = 0.1234567890123456789012345
            screens [
                { origin [{{x}}, {{y}}, {{z}}] }
            ]
            """)["screens"]);
        var origin = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: screens[0])["origin"]);

        return [.. origin.Select(selector: static component => component!.ToJsonString())];
    }

    // Three authored components of one origin and the canonical JSON each lowers to.
    private static readonly Dictionary<string, (string X, string Y, string Z, string[] Expected)> Origins = new(comparer: StringComparer.Ordinal) {
        ["an authored decimal keeps every digit through the lowering"] = (
            X: "0.1234567890123456789012345",
            Y: "-long",
            Z: "long",
            Expected: ["0.1234567890123456789012345", "-0.1234567890123456789012345", "0.1234567890123456789012345"]
        ),
        ["exact arithmetic over authored decimals stays exact"] = (
            X: "0.1 + 0.2",
            Y: "long - 0.0234567890123456789012345",
            Z: "1.1 * 1.1",
            Expected: ["0.3", "0.1", "1.21"]
        ),
        ["one value has one spelling"] = (
            X: "1.50",
            Y: "2.0",
            Z: "1.5e3",
            Expected: ["1.5", "2", "1500"]
        ),
        ["a magnitude a decimal cannot hold stays the double it was"] = (
            X: "1e-40",
            Y: "1e40",
            Z: "0.5",
            Expected: ["1E-40", "1E+40", "0.5"]
        ),
        // A comparison is worth 1 or 0, and it is decided on the exact values, so it agrees with the exact
        // arithmetic beside it: a difference the subtraction keeps is a difference the comparison sees.
        ["a comparison is decided on the exact values"] = (
            X: "1.0000000000000001 > 1",
            Y: "(1.0000000000000001 - 1) > 0",
            Z: "0.1 + 0.2 == 0.3",
            Expected: ["1", "1", "1"]
        ),
    };

    public static TheoryData<string> OriginNames() => new(values: Origins.Keys);
    [MemberData(nameof(OriginNames))]
    [Theory]
    public void AnAuthoredOriginLowersToItsCanonicalSpelling(string name) {
        var (x, y, z, expected) = Origins[name];

        Assert.Equal(
            actual: $"{name}: {string.Join(separator: ", ", values: LoweredOrigin(x: x, y: y, z: z))}",
            expected: $"{name}: {string.Join(separator: ", ", values: expected)}"
        );
    }
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
            operation: ExpressionOp.Multiply,
            result: out _,
            right: 0.1m
        ));
        Assert.False(condition: DocumentNumbers.TryExactArithmetic(
            left: decimal.MaxValue,
            operation: ExpressionOp.Add,
            result: out _,
            right: 0.5m
        ));
        Assert.True(condition: DocumentNumbers.TryExactArithmetic(
            left: 0.1m,
            operation: ExpressionOp.Add,
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
