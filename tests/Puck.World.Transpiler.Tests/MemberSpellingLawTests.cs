using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A member has one spelling wherever it stands — a call's argument, an object literal's property, a
/// block's property line — because lowering carries the model type each value fills and asks it.</summary>
/// <remarks>A construct with a lowering of its own reads its own grammar and stands at no position, so nothing
/// inside it is classified.</remarks>
public class MemberSpellingLawTests {
    private const string Pools = """
        schema: "puck.world.definition.v1"

        state {
            record Ball {
                live: Bool = true
            }
            record Paddle {
                hits: Int bounds(0..99) = 0
            }
            pool balls of Ball capacity(1) = [{ live: true }]
            pool paddles of Paddle capacity(2) = [{ hits: 0 }, { hits: 0 }]

            world {
                slot rally = 0
            }
        }

        """;
    private const string Rows = """
        schema: "puck.world.definition.v1"

        state {
            world {
                slot hp = 0
                table deck {
                    a = 1
                    b = 2
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
    private static Diagnostic Refusal(string source, string code) => Assert.Single(
        collection: Compile(source: source).Diagnostics,
        predicate: diagnostic => string.Equals(a: diagnostic.Code, b: code, comparisonType: StringComparison.Ordinal)
    );

    [InlineData("rule \"r\" {\n    mode: \"Level\"\n    hp = 1\n}", "mode: Level")]
    [InlineData("rule \"r\" {\n    forEach: \"deck\"\n    hp = 1\n}", "forEach: deck")]
    [InlineData("rule \"r\" {\n    transform sort(row: deck, by: [{ row: \"deck\", descending: false }])\n}", "row: deck")]
    [InlineData("host {\n    presentation: \"Windowed\"\n}", "presentation: Windowed")]
    [Theory]
    public void AStringLiteralIsRefusedWhereverTheModelSaysTheMemberIsWrittenBare(string body, string spelling) {
        var refusal = Refusal(code: PuckDiagnosticCodes.ArgumentWrittenBare, source: (Rows + body));

        Assert.Contains(actualString: refusal.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: $"write '{spelling}'");
    }
    [Fact]
    public void ABarePropertyLineLowersToTheMemberTheQuotedLineDid() {
        var rule = Document(source: (Rows + "rule \"r\" {\n    mode: Level\n    forEach: deck\n    hp = 1\n}"))["rules"]![0]!;

        Assert.Equal(expected: "Level", actual: rule["mode"]!.GetValue<string>());
        Assert.Equal(expected: "deck", actual: rule["forEach"]!.GetValue<string>());
    }
    [Fact]
    public void AnEnumerationWordIsReadInAnyCaseAndIsNeverACompileTimeBinding() {
        var host = Document(source: (Rows + "let Windowed = 3\n\nhost {\n    backend: auto\n    presentation: Windowed\n}"))["host"]!;

        Assert.Equal(expected: "auto", actual: host["backend"]!.GetValue<string>());
        Assert.Equal(expected: "Windowed", actual: host["presentation"]!.GetValue<string>());
    }
    [Fact]
    public void ABareNameReadsABindingOnlyWhereTheBindingHoldsANameOrAListOfNames() {
        var rules = Document(source: (Rows + "let deck = 7\nlet piles = [\"deck\"]\n\nrule \"r\" {\n    forEach: deck\n    zones: piles\n    hp = 1\n}"))["rules"]![0]!;

        Assert.Equal(expected: "deck", actual: rules["forEach"]!.GetValue<string>());
        Assert.Equal(expected: "deck", actual: rules["zones"]![0]!.GetValue<string>());
    }
    [Fact]
    public void ADirectionIsANameOfItsTopologyWrittenBare() {
        const string Ray = "patterns [\n    {\n        name: \"p\"\n        kind: Int\n        value: 1\n        symbols [{ name: \"any\", match: 1 }]\n        match: \"any\"\n    }\n]\n\nrule \"r\" {\n    transform setRay(row: deck, from: a, direction: N, pattern: p, value: 1)\n}";

        Assert.Equal(expected: "N", actual: Document(source: (Rows + Ray))["rules"]![0]!["effects"]![0]!["transform"]!["direction"]!.GetValue<string>());
        Assert.Contains(
            actualString: Refusal(code: PuckDiagnosticCodes.ArgumentWrittenBare, source: (Rows + Ray.Replace(comparisonType: StringComparison.Ordinal, newValue: "direction: \"N\"", oldValue: "direction: N"))).Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "write 'direction: N'"
        );
    }
    [Fact]
    public void AnInteractionBindsItsSidesSoAPoolFieldIsWrittenAsARuleWritesOne() {
        const string Strike = "interactions {\n    interactions [\n        {\n            coOccurrence: Distance\n            effects [\n                addState(state: right.hits, value: 1)\n            ]\n            left: balls\n            mode: Edge\n            name: \"strike\"\n            range: 1\n            right: paddles\n        }\n    ]\n}";
        var state = Document(source: (Pools + Strike))["interactions"]!["interactions"]![0]!["effects"]![0]!["state"]!;

        Assert.Equal(expected: "right", actual: state["binding"]!.GetValue<string>());
        Assert.Equal(expected: "hits", actual: state["field"]!.GetValue<string>());
        Assert.Contains(
            actualString: Refusal(code: PuckDiagnosticCodes.ArgumentWrittenBare, source: (Pools + Strike.Replace(comparisonType: StringComparison.Ordinal, newValue: "state: { binding: \"right\", field: \"hits\" }", oldValue: "state: right.hits"))).Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "binding.field"
        );
    }
    [Fact]
    public void AConstructWithItsOwnLoweringStandsAtNoPositionSoItsBodyIsNotClassified() {
        var (_, diagnostics) = Compile(source: (Rows + "state {\n    world {\n        row {\n            name: \"probe\"\n            kind: \"Int\"\n        }\n    }\n}"));

        Assert.DoesNotContain(collection: diagnostics, filter: static diagnostic => (diagnostic.Code is PuckDiagnosticCodes.ArgumentWrittenBare or PuckDiagnosticCodes.ArgumentWrittenQuoted));
    }
}
