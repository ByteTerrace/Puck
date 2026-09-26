using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A call that is neither a compile-time function nor an arm the world vocabulary declares is refused at the
/// call, once, as <c>PUCK041</c>, rather than reaching the document as an arm nothing reads: <c>min</c> is not
/// <c>minimum</c>, and no alias is taken, while the refusal names the one spelling.</summary>
public sealed class UnknownCompileTimeFunctionTests {
    private static void AssertRefusedOnce(string body, string needle, string? spelling) {
        var (_, diagnostics) = WorldSources.Lower(body: body);
        var refusal = Assert.Single(collection: diagnostics, predicate: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.BuiltinRefused));
        var line = ((WorldSources.Header + body).Split(separator: '\n').ToList().FindIndex(match: text => text.Contains(comparisonType: StringComparison.Ordinal, value: needle)) + 1);

        Assert.Equal(expected: line, actual: refusal.Span.Line);
        Assert.Contains(actualString: refusal.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "is not a compile-time function or a document arm");

        if (spelling is null) {
            Assert.DoesNotContain(actualString: refusal.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "spells it");
        } else {
            Assert.Contains(actualString: refusal.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: $"spells it '{spelling}'");
        }
    }

    [Fact]
    public void AnAbbreviatedFunctionIsRefusedAtTheCallNamingItsOneSpelling() {
        AssertRefusedOnce(
            body: "size: min(3, 4)\n",
            needle: "min(3, 4)",
            spelling: "minimum"
        );
        AssertRefusedOnce(
            body: "let small = min(3, 4)\nsize: small\n",
            needle: "min(3, 4)",
            spelling: "minimum"
        );
    }
    [Fact]
    public void ANameNothingDeclaresIsRefusedWithNoSpelling() => AssertRefusedOnce(
        body: "size: frobnicate(1)\n",
        needle: "frobnicate(1)",
        spelling: null
    );
    [Fact]
    public void TheOneSpellingFolds() {
        Assert.Equal(expected: 3L, actual: WorldSources.LowerClean(body: "size: minimum(3, 4)")["size"]?.GetValue<long>());
        Assert.Equal(expected: 3L, actual: WorldSources.LowerClean(body: "let small = minimum(3, 4)\nsize: small")["size"]?.GetValue<long>());
    }
}
