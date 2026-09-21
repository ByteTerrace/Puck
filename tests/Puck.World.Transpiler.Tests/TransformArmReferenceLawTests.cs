using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A row name written inside a <c>StateTransform</c> arm resolves like any other reference: an
/// unresolvable one is reported once, on the arm's own line, and a resolvable one draws nothing.</summary>
/// <remarks>The arm's fields are the only place a transform names a row — the effect node itself carries just the
/// arm — so a reference pass that stops at the effect sees no name at all.</remarks>
public class TransformArmReferenceLawTests {
    private const string Declared = "waste";
    private const string Missing = "missingZone";

    // The 1-based line of the statement under test.
    private static int LineOfDraw(string source) {
        var lines = source.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n');

        for (var index = 0; (index < lines.Length); index++) {
            if (lines[index].TrimStart().StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "draw "
            )) {
                return (index + 1);
            }
        }

        Assert.Fail(message: $"no line of the probe opens with 'draw':\n{source}");

        return 0;
    }
    private static IReadOnlyList<Diagnostic> Referenced(string source) {
        var diagnostics = new DiagnosticBag();
        var sourceMap = new SourceMap();
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: diagnostics,
            source: source,
            sourceMap: sourceMap
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(source)
        );
        PuckLinter.LintReferences(
            compilation.RequireJson(),
            sourceMap,
            diagnostics,
            sourcePath: "law.puck"
        );

        return [.. diagnostics.Where(predicate: static diagnostic => string.Equals(
            a: diagnostic.Code,
            b: PuckDiagnosticCodes.LintUnresolvedState,
            comparisonType: StringComparison.Ordinal
        ))];
    }
    private static string Source(string destination) => $$"""
        schema: "puck.world.definition.v1"

        state {
            world {
                table deck {
                    a = 1
                    b = 2
                }
                pile stock of deck capacity(2) {
                    a
                    b
                }
                pile waste of deck capacity(2) {
                }
            }
        }

        rule "r" {
            draw stock to {{destination}}
        }

        """;

    [Fact]
    public void AnUnresolvableRowInsideATransformArmIsReportedOnceOnTheArmsLine() {
        var source = Source(destination: Missing);
        var findings = Referenced(source: source);

        Assert.True(
            condition: (findings.Count == 1),
            userMessage: $"one name of the arm does not resolve and {findings.Count} findings were drawn: {string.Join(
                separator: " | ",
                values: findings.Select(selector: static finding => $"@{finding.Span.Line} {finding.Message}")
            )}"
        );
        Assert.Contains(
            actualString: findings[0].Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: Missing
        );
        Assert.Equal(
            actual: findings[0].Span.Line,
            expected: LineOfDraw(source: source)
        );
    }
    [Fact]
    public void AResolvableRowInsideATransformArmDrawsNothing() {
        Assert.Empty(collection: Referenced(source: Source(destination: Declared)));
    }
}
