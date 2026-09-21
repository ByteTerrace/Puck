using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>An interpolated string computes one name, one number or text, and never an expression: an expression
/// is written bare, and an interpolated string inside it is an atom the operand grammar reads as one token.</summary>
public class OperandAtomLawTests {
    private const string Rows = """
        schema: "puck.world.definition.v1"

        state {
            world {
                slot hp = 0
                table deck {
                    a0 = 1
                    a1 = 2
                    "b:1" = 3
                }
                table a1 {
                    x = 5
                }
            }
        }

        """;

    private static (JsonObject? Document, DiagnosticBag Diagnostics) Compile(string source) {
        var result = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        return (result.Json, result.Diagnostics);
    }
    private static JsonObject Document(string source) {
        var (document, diagnostics) = Compile(source: source);

        Assert.False(condition: diagnostics.HasErrors, userMessage: string.Join(separator: "\n", values: diagnostics.Select(selector: static diagnostic => diagnostic.ToString())));

        return document!;
    }
    private static JsonNode Effect(string body) => Document(source: (Rows + body))["rules"]![0]!["effects"]![0]!;

    [InlineData("$\"deck[a{i}]\"", "deck[$\"a{i}\"]")]
    [InlineData("$\"deck[{name}]\"", "deck[$\"{name}\"]")]
    [InlineData("$\"deck[({i})] + {i}\"", "deck[(i)] + i")]
    [InlineData("$\"a{i}[x] + {i + 1} + {2}\"", "$\"a{i}\"[x] + $\"{i + 1}\" + 2")]
    [InlineData("$\"$reduce:count:a{i}\"", "count($\"a{i}\")")]
    [Theory]
    public void AnExpressionBuiltAsTextIsRefusedWithItsBareSpelling(string written, string bare) {
        var refusal = Assert.Single(
            collection: Compile(source: (Rows + $"let i = 1\nlet name = \"a0\"\n\nrule \"r\" {{\n    setState(state: hp, expression: {written})\n}}")).Diagnostics,
            predicate: static diagnostic => string.Equals(a: diagnostic.Code, b: PuckDiagnosticCodes.ExpressionBuiltAsText, comparisonType: StringComparison.Ordinal)
        );

        Assert.Contains(actualString: refusal.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: $"write 'expression: {bare}'");
    }
    [Fact]
    public void AnAtomLowersToTheTokenItComputes() {
        var computed = Effect(body: "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"a{i}\"] + $\"{i + 1}\")\n}");
        var written = Effect(body: "rule \"r\" {\n    setState(state: hp, expression: deck[a1] + 2)\n}");

        Assert.True(condition: JsonNode.DeepEquals(node1: computed, node2: written), userMessage: computed.ToJsonString());
    }
    [Fact]
    public void AnAtomMayOpenAnOperand() {
        var computed = Effect(body: "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: $\"a{i}\"[x] + 1)\n}");
        var written = Effect(body: "rule \"r\" {\n    setState(state: hp, expression: a1[x] + 1)\n}");

        Assert.True(condition: JsonNode.DeepEquals(node1: computed, node2: written), userMessage: computed.ToJsonString());
    }
    [Fact]
    public void AnAtomThatComputesMoreThanABareNameIsReadAsOneName() {
        var computed = Effect(body: "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"b:{i}\"])\n}");
        var written = Effect(body: "rule \"r\" {\n    setState(state: hp, expression: deck[`b:1`])\n}");

        Assert.True(condition: JsonNode.DeepEquals(node1: computed, node2: written), userMessage: computed.ToJsonString());
    }
    [Fact]
    public void WhatAnAtomComputesIsNeverReadAgainAsABinding() {
        var computed = Effect(body: "let i = 1\nlet a1 = 99\n\nrule \"r\" {\n    setState(state: hp, expression: 1 + $\"a{i}\")\n}");

        Assert.DoesNotContain(expectedSubstring: "99", actualString: computed.ToJsonString(), comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void OneBareWordInAKeysPositionIsThatKeyAsItIsInsideABracket() {
        var effect = Effect(body: "let a0 = \"a1\"\n\nrule \"r\" {\n    setState(state: deck, key: a0, expression: deck[a0])\n}");

        Assert.Equal(expected: "a0", actual: effect["key"]!.GetValue<string>());
        Assert.DoesNotContain(expectedSubstring: "a1", actualString: effect.ToJsonString(), comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void AnInterpolatedStringStillComputesAWholeNameOrKey() {
        var effect = Effect(body: "let i = 1\n\nrule \"r\" {\n    setState(state: $\"a{i}\", key: $\"b:{i}\", expression: 1)\n}");

        Assert.Equal(expected: "a1", actual: effect["state"]!.GetValue<string>());
        Assert.Equal(expected: "b:1", actual: effect["key"]!.GetValue<string>());
    }
    [Fact]
    public void AStatementLineAndACallBindTheSameAtom() {
        var computed = Effect(body: "let i = 1\n\nrule \"r\" {\n    hp = deck[$\"a{i}\"]\n}");
        var called = Effect(body: "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"a{i}\"])\n}");
        Assert.True(condition: JsonNode.DeepEquals(node1: computed, node2: called), userMessage: computed.ToJsonString());
    }
    [Fact]
    public void AnUnusedBindingIsNotReportedWhereOnlyAnAtomReadsIt() {
        var (_, diagnostics) = Compile(source: (Rows + "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"a{i}\"])\n}"));

        Assert.DoesNotContain(collection: WorldSourceLint(source: (Rows + "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"a{i}\"])\n}")), filter: static diagnostic => string.Equals(a: diagnostic.Code, b: PuckDiagnosticCodes.LintUnusedLet, comparisonType: StringComparison.Ordinal));
        Assert.False(condition: diagnostics.HasErrors);
    }
    [Fact]
    public void FormattingLeavesAnOperandsAtomsAsWritten() {
        var source = (Rows + "let i = 1\n\nrule \"r\" {\n  setState(state: hp, expression: deck[$\"a{i}\"] + $\"{i + 1}\")\n}\n");
        var printed = PuckFormat.Format(source: source);

        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "expression: deck[$\"a{i}\"] + $\"{i + 1}\"");
    }
    [Fact]
    public void AnImportAliasReachesABindingReadInsideAnAtom() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-atom-import-");

        try {
            File.WriteAllText(
                contents: "let index = 1\nmodule bump() {\n    state { world { slot hp = 0\n table deck { a1 = 2 } } }\n    rule \"bump\" {\n        setState(state: hp, expression: deck[$\"a{index}\"])\n    }\n}",
                path: Path.Combine(path1: directory.FullName, path2: "library.puck")
            );

            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: "schema: \"puck.world.definition.v1\"\n\nimport \"library.puck\" as lib\n\nuse lib.bump as first()\n", path: rootPath);

            var compilation = WorldCompiler.CompileFile(rootPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
            Assert.Contains(
                actualString: compilation.RequireJson()["rules"]![0]!["effects"]![0]!["expression"]!.ToJsonString(),
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "\"a1\""
            );
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void AtomMarkersCannotCollideWithBindingsOrComputedNames() {
        var computed = Effect(body: "let __atom0__ = 99\nrule \"r\" { setState(state: hp, expression: 1 + $\"a1\") }");
        var written = Effect(body: "rule \"r\" { setState(state: hp, expression: 1 + a1) }");

        Assert.True(condition: JsonNode.DeepEquals(node1: computed, node2: written), userMessage: computed.ToJsonString());

        computed = Effect(body: "rule \"r\" { setState(state: hp, expression: deck[$\"__atom1__\"] + deck[$\"a1\"]) }");
        written = Effect(body: "rule \"r\" { setState(state: hp, expression: deck[__atom1__] + deck[a1]) }");
        Assert.True(condition: JsonNode.DeepEquals(node1: computed, node2: written), userMessage: computed.ToJsonString());
    }

    private static DiagnosticBag WorldSourceLint(string source) => Validation.WorldSourceDiagnostics.Diagnose(
        cancellationToken: TestContext.Current.CancellationToken,
        source: source
    );
}
