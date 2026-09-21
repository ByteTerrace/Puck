using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A <c>transform</c> written with a result label is refused by name, and the refusal names the spelling to
/// write instead.</summary>
/// <remarks>A transform's destination travels inside the call's own arguments, so there is nothing for a label to
/// name and no reader that would take one.</remarks>
public class TransformResultLabelRefusalLawTests {
    private const string Labelled = """
        transform wipe = boardCombine(row: deck, operation: Copy)
        """;
    private const string Unlabelled = """
        transform boardCombine(row: deck, operation: Copy)
        """;

    private static DiagnosticBag Compile(string statement) => WorldCompiler.Compile(
        cancellationToken: TestContext.Current.CancellationToken,
        source: Source(statement: statement)
    ).Diagnostics;
    // The 1-based line the probe writes its transform on.
    private static int LineOfTransform(string statement) {
        var lines = Source(statement: statement).ReplaceLineEndings(replacementText: "\n").Split(separator: '\n');

        for (var index = 0; (index < lines.Length); index++) {
            if (lines[index].TrimStart().StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "transform "
            )) {
                return (index + 1);
            }
        }

        Assert.Fail(message: "no line of the probe opens with 'transform'");

        return 0;
    }
    private static string Source(string statement) => $$"""
        schema: "puck.world.definition.v1"

        state {
            world {
                table deck {
                    a = 1
                    b = 2
                }
            }
        }

        rule "r" {
            {{statement}}
        }

        """;

    [Fact]
    public void TheLabelledSpellingIsRefusedOnItsOwnLineAndNamesWhatToWriteInstead() {
        var diagnostics = Compile(statement: Labelled);
        var refusals = diagnostics
            .Where(predicate: static diagnostic => string.Equals(
                a: diagnostic.Code,
                b: PuckDiagnosticCodes.TransformResultLabel,
                comparisonType: StringComparison.Ordinal
            ))
            .ToArray();

        Assert.True(
            condition: (refusals.Length == 1),
            userMessage: $"{PuckDiagnosticCodes.TransformResultLabel} is drawn {refusals.Length} times: {string.Join(
                separator: " | ",
                values: diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code} @{diagnostic.Span.Line} {diagnostic.Message}")
            )}"
        );
        Assert.Equal(
            actual: refusals[0].Span.Line,
            expected: LineOfTransform(statement: Labelled)
        );
        // The refusal has to carry the current spelling, not merely reject the retired one.
        Assert.Contains(
            actualString: refusals[0].Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "transform <call>(...)"
        );
        Assert.Contains(
            actualString: refusals[0].Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "transform wipe = "
        );
    }
    [Fact]
    public void TheUnlabelledSpellingIsTheOneTheVocabularyTakes() {
        var diagnostics = Compile(statement: Unlabelled);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(Source(statement: Unlabelled))
        );
    }
}
