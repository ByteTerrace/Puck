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

    private static JsonNode Effect(string body) => WorldSources.LowerSourceClean(source: (Rows + body))["rules"]![0]!["effects"]![0]!;

    [InlineData("$\"deck[a{i}]\"", "deck[$\"a{i}\"]")]
    [InlineData("$\"deck[{name}]\"", "deck[$\"{name}\"]")]
    [InlineData("$\"deck[({i})] + {i}\"", "deck[(i)] + i")]
    [InlineData("$\"a{i}[x] + {i + 1} + {2}\"", "$\"a{i}\"[x] + $\"{i + 1}\" + 2")]
    [InlineData("$\"$reduce:count:a{i}\"", "count($\"a{i}\")")]
    [Theory]
    public void AnExpressionBuiltAsTextIsRefusedWithItsBareSpelling(string written, string bare) {
        var refusal = Assert.Single(
            collection: WorldSources.Compile(source: (Rows + $"let i = 1\nlet name = \"a0\"\n\nrule \"r\" {{\n    setState(state: hp, expression: {written})\n}}")).Diagnostics,
            predicate: static diagnostic => string.Equals(a: diagnostic.Code, b: PuckDiagnosticCodes.ExpressionBuiltAsText, comparisonType: StringComparison.Ordinal)
        );

        Assert.Contains(actualString: refusal.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: $"write 'expression: {bare}'");
    }

    // Each atom-bearing source and the source that spells what its atoms compute by hand; the two lower alike.
    private static readonly Dictionary<string, (string Computed, string Written)> Atoms = new(comparer: StringComparer.Ordinal) {
        ["an atom lowers to the token it computes"] = (
            Computed: "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"a{i}\"] + $\"{i + 1}\")\n}",
            Written: "rule \"r\" {\n    setState(state: hp, expression: deck[a1] + 2)\n}"
        ),
        ["an atom may open an operand"] = (
            Computed: "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: $\"a{i}\"[x] + 1)\n}",
            Written: "rule \"r\" {\n    setState(state: hp, expression: a1[x] + 1)\n}"
        ),
        ["an atom computing more than a bare name is read as one name"] = (
            Computed: "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"b:{i}\"])\n}",
            Written: "rule \"r\" {\n    setState(state: hp, expression: deck[`b:1`])\n}"
        ),
        ["a statement line and a call bind the same atom"] = (
            Computed: "let i = 1\n\nrule \"r\" {\n    hp = deck[$\"a{i}\"]\n}",
            Written: "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"a{i}\"])\n}"
        ),
        ["an assignment target's key computes as the same key read does"] = (
            Computed: "let i = 1\n\nrule \"r\" {\n    deck[$\"a{i}\"] = 4\n}",
            Written: "rule \"r\" {\n    deck[a1] = 4\n}"
        ),
        ["an addition target's key computes as the same key read does"] = (
            Computed: "let i = 1\n\nrule \"r\" {\n    deck[$\"a{i}\"] += deck[$\"a{i}\"]\n}",
            Written: "rule \"r\" {\n    deck[a1] += deck[a1]\n}"
        ),
        ["a target's atom computing more than a bare name is one key"] = (
            Computed: "let i = 1\n\nrule \"r\" {\n    deck[$\"b:{i}\"] = 4\n}",
            Written: "rule \"r\" {\n    deck[`b:1`] = 4\n}"
        ),
        ["an atom naming one of the rule's locals reads the local"] = (
            Computed: "rule \"r\" {\n    for i in range(0, 2) {\n        local $\"at{i}\" = deck[$\"a{i}\"]\n    }\n    hp = $\"at{1}\" - $\"at{0}\"\n}",
            Written: "rule \"r\" {\n    local at0 = deck[a0]\n    local at1 = deck[a1]\n    hp = at1 - at0\n}"
        ),
        ["an atom naming no local is a row even beside locals"] = (
            Computed: "let i = 1\n\nrule \"r\" {\n    local at0 = 1\n    hp = $\"a{i}\"[x] + at0\n}",
            Written: "rule \"r\" {\n    local at0 = 1\n    hp = a1[x] + at0\n}"
        ),
    };

    public static TheoryData<string> AtomNames() => new(values: Atoms.Keys);
    [MemberData(nameof(AtomNames))]
    [Theory]
    public void AnAtomLowersAsItsHandWrittenSpelling(string name) {
        var (computed, written) = Atoms[name];
        var lowered = Effect(body: computed);

        Assert.True(condition: JsonNode.DeepEquals(node1: lowered, node2: Effect(body: written)), userMessage: $"{name}: {lowered.ToJsonString()}");
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
    public void AnUnusedBindingIsNotReportedWhereOnlyAnAtomReadsIt() {
        var diagnostics = WorldSources.Compile(source: (Rows + "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"a{i}\"])\n}")).Diagnostics;

        Assert.DoesNotContain(collection: WorldSourceLint(source: (Rows + "let i = 1\n\nrule \"r\" {\n    setState(state: hp, expression: deck[$\"a{i}\"])\n}")), filter: static diagnostic => string.Equals(a: diagnostic.Code, b: PuckDiagnosticCodes.LintUnusedLet, comparisonType: StringComparison.Ordinal));
        Assert.False(condition: diagnostics.HasErrors);
    }
    [Fact]
    public void FormattingLeavesAnOperandsAtomsAsWritten() {
        var source = (Rows + "let i = 1\n\nrule \"r\" {\n  setState(state: hp, expression: deck[$\"a{i}\"] + $\"{i + 1}\")\n  deck[$\"a{i}\"] = 1\n}\n");
        var printed = PuckFormat.Format(source: source);

        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "expression: deck[$\"a{i}\"] + $\"{i + 1}\"");
        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "deck[$\"a{i}\"] = 1");
    }
    [Fact]
    public void AnUnusedBindingIsNotReportedWhereOnlyATargetsAtomReadsIt() {
        var source = (Rows + "let i = 1\n\nrule \"r\" {\n    deck[$\"a{i}\"] = 4\n}");

        Assert.DoesNotContain(collection: WorldSourceLint(source: source), filter: static diagnostic => string.Equals(a: diagnostic.Code, b: PuckDiagnosticCodes.LintUnusedLet, comparisonType: StringComparison.Ordinal));
        Assert.False(condition: WorldSources.Compile(source: source).Diagnostics.HasErrors);
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
