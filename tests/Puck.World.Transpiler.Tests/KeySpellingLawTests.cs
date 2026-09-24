using Puck.State;
using Puck.Transpiler.Formatting;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A cell key has one spelling: an assignment's target key and the same key read inside an operand print the
/// same way, from the formatter and from the decompiler alike. Parentheses around an expression key are dropped where
/// the bare text reads back as the same expression key (<c>well[(py + i)]</c> prints <c>well[py + i]</c>) and kept
/// where it would read back as a literal name, a number or a cell indirection (<c>well[(level)]</c>).</summary>
public sealed class KeySpellingLawTests {
    private static string Source(string key) => $$"""
        schema: "puck.world.definition.v1"
        documentId: "keys"

        state {
            world {
                slot py = 0
                slot i = 0
                slot level = 0
                table well capacity(8)
            }
        }

        rule "write" {
            when well[{{key}}] >= 0
            well[{{key}}] = well[{{key}}] + 1
        }
        """;
    // The target key and the read keys of the rule's one assignment and its gate, as printed.
    private static (string Target, string Read, string Gate) Keys(string printed) {
        var assignment = printed.Split(separator: '\n').Select(selector: static line => line.Trim()).Single(predicate: static line => (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "well[") && line.Contains(comparisonType: StringComparison.Ordinal, value: " = ")));
        var gate = printed.Split(separator: '\n').Select(selector: static line => line.Trim()).Single(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "when well["));
        var equals = assignment.IndexOf(comparisonType: StringComparison.Ordinal, value: " = ");
        var target = assignment[..equals];
        var read = assignment[(equals + 3)..];

        return (
            target["well[".Length..^1],
            read["well[".Length..^" + 1".Length][..^1],
            gate["when well[".Length..^" >= 0".Length][..^1]
        );
    }

    public static TheoryData<string> Spellings() => [
        "py + i",
        "(py + i)",
        "((py + i))",
        "(py + i) * 2",
        "level",
        "(level)",
        "3",
        "(3)",
        "well[i]",
        "(well[i])",
        "(well[i]) + 1",
    ];
    [MemberData(nameof(Spellings))]
    [Theory]
    public void TheFormatterPrintsATargetKeyAndAReadKeyTheSameWay(string key) {
        var printed = PuckPrinter.Format(source: Source(key: key), vocabulary: WorldDocumentVocabulary.Instance);

        Assert.NotNull(@object: printed.Value);

        var (target, read, gate) = Keys(printed: printed.Value);

        Assert.Equal(actual: read, expected: target);
        Assert.Equal(actual: gate, expected: target);
    }
    [MemberData(nameof(Spellings))]
    [Theory]
    public void TheDecompilerPrintsATargetKeyAndAReadKeyTheSameWay(string key) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: Source(key: key)
        );

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(Source(key: key)));

        var (target, read, gate) = Keys(printed: WorldDecompiler.Decompile(root: compilation.RequireJson()));

        Assert.Equal(actual: read, expected: target);
        Assert.Equal(actual: gate, expected: target);
    }
    // Both printers agree on the one spelling, so formatting a decompiled source changes none of its keys.
    [MemberData(nameof(Spellings))]
    [Theory]
    public void TheFormatterAndTheDecompilerAgreeOnTheSpelling(string key) {
        var formatted = PuckPrinter.Format(source: Source(key: key), vocabulary: WorldDocumentVocabulary.Instance);
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: Source(key: key)
        );

        Assert.Equal(
            expected: Keys(printed: WorldDecompiler.Decompile(root: compilation.RequireJson())),
            actual: Keys(printed: formatted.Value!)
        );
    }
    // The mutation proof: a read printed as written — the formatter before its keys took the target's spelling —
    // disagrees with the target's spelling of the same key, and the law's comparison sees it.
    [Fact]
    public void TheLawSeesAReadPrintedAsWritten() {
        var target = new System.Text.StringBuilder();

        Assert.True(condition: ExpressionSpelling.TryParseKey(error: out _, key: out var key, text: "(py + i)"));
        ExpressionSpelling.AppendSourceKey(into: target, key: key);

        Assert.NotEqual(expected: "(py + i)", actual: target.ToString());
        Assert.Equal(expected: target.ToString(), actual: PuckPrinter.OperandKeys(text: "well[(py + i)]")["well[".Length..^1]);
    }
}
