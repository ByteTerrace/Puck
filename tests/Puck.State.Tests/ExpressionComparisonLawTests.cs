using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

/// <summary>A comparison is an <see cref="ExpressionOp"/> in the operator table's comparison subset: one flip, one
/// spelling, one evaluation, and a document member that names nothing else.</summary>
public sealed class ExpressionComparisonLawTests {
    private sealed record Holder([property: JsonConverter(typeof(ExpressionComparisonJsonConverter))] ExpressionOp Comparison);

    private static readonly long[] Samples = [-3L, -1L, 0L, 1L, 2L];

    [Fact]
    public void TheSubsetIsExactlyTheOperatorTablesComparisonRows() => Assert.Equal(
        ExpressionOperators.All
            .Where(predicate: static row => (row.Signature == ExpressionSignature.Comparison))
            .Select(selector: static row => row.Operation),
        ExpressionComparisons.All
    );
    [Fact]
    public void AFlipExchangesTheOperandsAndIsItsOwnInverse() {
        foreach (var comparison in ExpressionComparisons.All) {
            Assert.Equal(
                comparison,
                comparison.Flip().Flip()
            );

            foreach (var left in Samples) {
                foreach (var right in Samples) {
                    Assert.Equal(
                        comparison.Holds(
                            expected: FixedQ4816.FromRawBits(value: right),
                            value: FixedQ4816.FromRawBits(value: left)
                        ),
                        comparison.Flip().Holds(
                            expected: FixedQ4816.FromRawBits(value: left),
                            value: FixedQ4816.FromRawBits(value: right)
                        )
                    );
                }
            }
        }
    }
    [Fact]
    public void AComparisonHoldsExactlyWhenTheArithmeticComparisonYieldsOne() {
        foreach (var comparison in ExpressionComparisons.All) {
            foreach (var left in Samples) {
                foreach (var right in Samples) {
                    Assert.True(condition: ExpressionArithmetic.TryBinary(
                        kind: CellKind.Int,
                        left: left,
                        operation: comparison,
                        right: right,
                        value: out var pushed
                    ));
                    Assert.Equal(
                        (pushed == 1L),
                        comparison.Holds(
                            expected: FixedQ4816.FromRawBits(value: right),
                            value: FixedQ4816.FromRawBits(value: left)
                        )
                    );
                }
            }
        }
    }
    [Fact]
    public void ASymbolReadsBackAsItsComparison() {
        foreach (var comparison in ExpressionComparisons.All) {
            Assert.True(condition: ExpressionComparisons.TryParseSymbol(
                comparison: out var parsed,
                symbol: comparison.Symbol()
            ));
            Assert.Equal(
                actual: parsed,
                expected: comparison
            );
        }

        Assert.False(condition: ExpressionComparisons.TryParseSymbol(
            comparison: out _,
            symbol: "+"
        ));
    }
    [Fact]
    public void AnOperationThatIsNotAComparisonHasNoFlipSymbolOrAnswer() {
        Assert.False(condition: ExpressionOp.Add.IsComparison());
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => ExpressionOp.Add.Flip());
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => ExpressionOp.Add.Symbol());
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => ExpressionOp.Add.Holds(
            expected: FixedQ4816.Zero,
            value: FixedQ4816.Zero
        ));
    }
    [InlineData("Equal", ExpressionOp.Equal)]
    [InlineData("GreaterOrEqual", ExpressionOp.GreaterOrEqual)]
    [Theory]
    public void ADocumentComparisonReadsAndWritesByItsMemberName(string name, ExpressionOp comparison) {
        var json = $"{{\"Comparison\":\"{name}\"}}";

        Assert.Equal(
            comparison,
            JsonSerializer.Deserialize<Holder>(json: json)!.Comparison
        );
        Assert.Equal(
            json,
            JsonSerializer.Serialize(value: new Holder(Comparison: comparison))
        );
    }
    // A comparison member names one of six operations: another operation's name, another casing, and a number
    // are each refused rather than read as some operation.
    [Theory]
    [InlineData("\"Add\"")]
    [InlineData("\"Select\"")]
    [InlineData("\"equal\"")]
    [InlineData("\"GreaterThan\"")]
    [InlineData("17")]
    public void ADocumentComparisonNamingAnythingElseIsRefused(string value) {
        var error = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<Holder>(json: $"{{\"Comparison\":{value}}}"));

        Assert.Contains(
            actualString: error.Message,
            expectedSubstring: "A comparison is one of"
        );
    }
    [Fact]
    public void EveryPredicateComparisonMemberReadsThroughTheComparisonConverter() {
        foreach (var type in ((Type[])[typeof(ActionPredicate.CompareState), typeof(ActionPredicate.CompareValue)])) {
            var member = type.GetProperty(name: nameof(ActionPredicate.CompareState.Comparison))!;

            Assert.Equal(
                typeof(ExpressionComparisonJsonConverter),
                Assert.Single(collection: member.GetCustomAttributes(
                    attributeType: typeof(JsonConverterAttribute),
                    inherit: false
                ).Cast<JsonConverterAttribute>()).ConverterType
            );
        }
    }
    [Fact]
    public void WritingANonComparisonIsRefused() => Assert.Throws<JsonException>(testCode: static () => JsonSerializer.Serialize(value: new Holder(Comparison: ExpressionOp.Add)));
}
