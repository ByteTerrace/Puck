using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A call argument has one spelling, decided by what the document model says the member holds: a name, a
/// cell key, a value expression and a word of a closed vocabulary are written bare, and text is a string
/// literal.</summary>
/// <remarks>The form comes from <see cref="WorldCallArguments"/>, which reads the model, so no list of members
/// stands beside it. An interpolated string computes an argument's text at compile time and is admitted in every
/// form.</remarks>
public class ArgumentSpellingLawTests {
    private static (JsonObject? Document, DiagnosticBag Diagnostics) Compile(string effect) {
        var result = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: Source(effect: effect)
        );

        return (result.Json, result.Diagnostics);
    }
    private static JsonObject Effect(string effect) {
        var (document, diagnostics) = Compile(effect: effect);

        Assert.False(condition: diagnostics.HasErrors, userMessage: string.Join(separator: "\n", values: diagnostics.Select(selector: static diagnostic => diagnostic.ToString())));

        return Assert.IsType<JsonObject>(@object: document!["rules"]![0]!["effects"]![0]);
    }
    private static Diagnostic Refusal(string effect, string code) {
        var (_, diagnostics) = Compile(effect: effect);

        return Assert.Single(collection: diagnostics, predicate: diagnostic => string.Equals(a: diagnostic.Code, b: code, comparisonType: StringComparison.Ordinal));
    }
    private static string Source(string effect) => $$"""
        schema: "puck.world.definition.v1"

        state {
            world {
                slot hp = 0
                table deck {
                    a = 1
                    b = 2
                }
                table bag {
                    a = 1
                }
            }
        }

        rule "r" {
            local next = hp + 1
            {{effect}}
        }

        """;

    [Fact]
    public void ArmsThatShareADiscriminatorAgreeOnEveryMemberTheyShare() => Assert.Empty(collection: WorldCallArguments.Conflicts);
    [InlineData("setState(state: \"hp\", value: 1)", "state: hp")]
    [InlineData("setState(state: deck, key: \"a\", value: 1)", "key: a")]
    [InlineData("setState(state: deck, key: \"$expr:hp + 1\", value: 1)", "key: hp + 1")]
    [InlineData("setState(state: hp, expression: \"hp + 1\")", "expression: hp + 1")]
    [InlineData("setState(state: hp, expression: \"$local:next\")", "expression: next")]
    [InlineData("transform transfer(from: deck, to: bag, selector: \"First\")", "selector: First")]
    [Theory]
    public void AStringLiteralWhereAnArgumentIsWrittenBareIsRefusedAndTheRefusalNamesTheBareSpelling(string effect, string spelling) {
        var refusal = Refusal(code: PuckDiagnosticCodes.ArgumentWrittenBare, effect: effect);

        Assert.Contains(actualString: refusal.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: $"write '{spelling}'");
        Assert.Equal(expected: 18, actual: refusal.Span.Line);
    }
    [Fact]
    public void ABareWordWhereAnArgumentIsTextIsRefusedAndTheRefusalNamesTheQuotedSpelling() {
        var refusal = Refusal(code: PuckDiagnosticCodes.ArgumentWrittenQuoted, effect: "designate(key: $each, register: companion, targetKey: $right)");

        Assert.Contains(actualString: refusal.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "write 'register: \"companion\"'");
    }
    [Fact]
    public void AColonChannelWrittenBareIsRefusedByTheCallThatReplacesIt() {
        var refusal = Refusal(code: PuckDiagnosticCodes.OperandColonChannel, effect: "setState(state: hp, expression: $local:next)");

        Assert.Contains(actualString: refusal.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "write 'next'");
    }
    [Fact]
    public void ABareExpressionReadsTheRulesLocalsAndABareKeyReadsTheKeyGrammar() {
        var effect = Effect(effect: "setState(state: deck, key: (hp + 1), expression: next * 2)");

        Assert.Equal(expected: "deck", actual: effect["state"]!.GetValue<string>());
        Assert.Equal(expected: "expr", actual: effect["key"]!["channel"]!.GetValue<string>());
        Assert.Equal(expected: "local", actual: effect["expression"]!["instructions"]![0]!["name"]!["channel"]!.GetValue<string>());
    }
    [Fact]
    public void ABareReservedReferenceLowersToTheNodeTheDocumentHolds() {
        var effect = Effect(effect: "transform transfer(from: deck, to: bag, selector: Key, key: deck[a])");
        var key = effect["transform"]!["key"]!;

        Assert.Equal(expected: "cell", actual: key["channel"]!.GetValue<string>());
        Assert.Equal(expected: "First", actual: Effect(effect: "transform transfer(from: deck, to: bag, selector: First)")["transform"]!["selector"]!.GetValue<string>());
    }
    [Fact]
    public void AnInterpolatedStringComputesANameAKeyOrAnAtomOfAnExpression() {
        var effect = Effect(effect: "setState(state: $\"deck\", key: $\"{1}\", expression: hp + $\"{1}\")");

        Assert.Equal(expected: "deck", actual: effect["state"]!.GetValue<string>());
        Assert.Equal(expected: "1", actual: effect["key"]!.GetValue<string>());
    }
    [Fact]
    public void AnAbsentMemberIsNullInEveryForm() {
        var (document, diagnostics) = Compile(effect: "setState(state: hp, fromState: null, value: 1)");

        Assert.False(condition: diagnostics.HasErrors);
        Assert.Null(@object: document!["rules"]![0]!["effects"]![0]!["fromState"]);
    }
    [Fact]
    public void ADecompiledCallWritesEveryArgumentInItsOneSpellingAndCompilesBackToTheSameDocument() {
        var (document, diagnostics) = Compile(effect: "setState(state: deck, key: (hp + 1), expression: next * 2, target: Self)");

        Assert.False(condition: diagnostics.HasErrors);

        var printed = WorldDecompiler.Decompile(root: document!);
        var recompiled = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: printed);

        Assert.False(condition: recompiled.Diagnostics.HasErrors, userMessage: printed);
        Assert.True(condition: JsonNode.DeepEquals(node1: document, node2: recompiled.Json), userMessage: printed);
        Assert.DoesNotContain(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "state: \"");
        Assert.DoesNotContain(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "$expr:");
    }
}
