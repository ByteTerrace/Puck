using System.Globalization;
using Puck.State;
using Puck.State.Rules;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A document value and a rule operand read their infix operators from the one table,
/// <see cref="ExpressionOperators"/>: the same text groups the same way and folds to what the rule evaluator
/// computes.</summary>
public sealed class OperatorTableLawTests {
    private static readonly long[] Operands = [-9L, -1L, 0L, 1L, 2L, 3L, 7L, 63L, 64L];
    private static readonly RuleCompileContext Context = new(
        catalog: StateCatalog.Compile(section: null),
        generators: null,
        patterns: null,
        section: null,
        simulationRateHz: 240,
        tables: null,
        vocabulary: RuleVocabulary.Core
    );

    private static string Spell(long value) => ((value < 0L)
        ? $"({value.ToString(provider: CultureInfo.InvariantCulture)})"
        : value.ToString(provider: CultureInfo.InvariantCulture));
    // Returns the rule evaluator's raw result for an operand's text, or null where the rule refuses it.
    private static long? Rule(string text, CellKind kind = CellKind.Int) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                program: out var parsed,
                text: text
            ),
            userMessage: $"{text}: {error}"
        );

        var compiled = RuleCompiler.CompileExpression(
            context: Context,
            expression: parsed,
            kind: kind,
            ruleName: "law",
            verb: "law"
        );

        return (RuleExpressions.TryEvaluate(
            fault: out _,
            kind: kind,
            program: compiled,
            reader: null,
            value: out var value
        )
            ? value
            : null
        );
    }
    // Returns the document's folded value for an expression's text, or null where the document refuses it.
    private static long? Document(string text) {
        var (json, diagnostics) = WorldSources.Lower(body: $"value: {text}\n");

        if (diagnostics.HasErrors) {
            return null;
        }

        Assert.True(
            condition: DocumentNumbers.TryInteger(
                node: json["value"],
                number: out var value
            ),
            userMessage: $"{text} folded to {json["value"]?.ToJsonString()}"
        );

        return value;
    }

    [Fact]
    public void AShiftFoldsInALet() {
        var lowered = WorldSources.LowerClean(body: """
            let mask = 1 << 3

            value: mask
            """);

        Assert.Equal(
            expected: 8L,
            actual: lowered["value"]?.GetValue<long>()
        );
    }
    [Fact]
    public void EveryInfixOperatorFoldsAsTheRuleEvaluatesIt() {
        foreach (var row in ExpressionOperators.Infix) {
            foreach (var left in Operands) {
                foreach (var right in Operands) {
                    // A document folds '/' over exact numbers where an Int rule truncates, so only an exact
                    // quotient is a question both languages answer the same way.
                    if ((row.Symbol == "/") && (right != 0L) && ((left % right) != 0L)) {
                        continue;
                    }

                    var text = $"{Spell(value: left)} {row.Symbol} {Spell(value: right)}";
                    var rule = Rule(text: text);
                    var document = Document(text: text);

                    Assert.True(
                        condition: (rule == document),
                        userMessage: $"{text}: rule {rule}, document {document}"
                    );
                }
            }
        }
    }
    // A fractional value folds as the rule's Fixed reading of the operator, at the precision the document holds, so
    // wherever the exact answer is a Q48.16 value the two agree bit for bit.
    [Fact]
    public void EveryNumericOperatorFoldsAsTheRuleEvaluatesItOverFixed() {
        decimal[] fractions = [-2.5m, -0.75m, 0m, 0.25m, 0.5m, 1m, 1.5m, 3m];

        foreach (var row in ExpressionOperators.Infix.Where(predicate: static row => (row.Signature == ExpressionSignature.Numeric))) {
            foreach (var left in fractions) {
                foreach (var right in fractions) {
                    var text = $"({left.ToString(provider: CultureInfo.InvariantCulture)}) {row.Symbol} ({right.ToString(provider: CultureInfo.InvariantCulture)})";

                    var (json, diagnostics) = WorldSources.Lower(body: $"value: {text}\n");
                    var rule = Rule(kind: CellKind.Fixed, text: text);

                    if (diagnostics.HasErrors) {
                        Assert.True(
                            condition: (rule is null),
                            userMessage: $"{text}: the document refuses it and the rule reads {rule}"
                        );

                        continue;
                    }

                    // A quotient Q48.16 cannot hold exactly is rounded by the rule and kept by the document, as a
                    // double when a decimal cannot hold it either.
                    if (
                        !DocumentNumbers.TryExact(
                            node: json["value"],
                            number: out var folded
                        ) ||
                        (decimal.Truncate(d: (folded * 65536m)) != (folded * 65536m))
                    ) {
                        continue;
                    }

                    var raw = (folded * 65536m);

                    Assert.True(
                        condition: (rule == ((long)raw)),
                        userMessage: $"{text}: rule {rule}, document {folded}"
                    );
                }
            }
        }
    }
    [Fact]
    public void AnOperatorTheTableDoesNotSpellRefuses() {
        var diagnostics = new DiagnosticBag();
        var scope = new DocumentScope(
            diagnostics: diagnostics,
            vocabulary: WorldDocumentVocabulary.Instance
        );
        var folded = DocumentLowering.LowerValue(
            expr: new BinaryExpressionNode(
                Left: new LiteralExpressionNode(Value: 1L),
                Operator: "<>",
                Right: new LiteralExpressionNode(Value: 2L)
            ),
            scope: scope
        );

        Assert.Null(@object: folded);
        Assert.Contains(
            collection: diagnostics,
            filter: static diagnostic => diagnostic.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "'<>' is not an infix operator"
            )
        );
    }
    [InlineData("1 | 2 & 3 ^ 4")]
    [InlineData("1 << 2 + 3")]
    [InlineData("1 + 2 * 3 << 1")]
    [InlineData("5 & 3 == 1")]
    [InlineData("2 < 3 == 1")]
    [InlineData("8 >> 1 >> 1")]
    [InlineData("-1 >>> 60")]
    [InlineData("~5 & 7")]
    [InlineData("10 - 3 - 2")]
    [Theory]
    public void MixedOperatorsGroupAsTheRuleGroupsThem(string text) {
        Assert.Equal(
            expected: Rule(text: text),
            actual: Document(text: text)
        );
    }
}
