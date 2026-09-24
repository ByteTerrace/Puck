using System.Text;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckPrinter"/> against the `.puck` DSL sugar wave's new constructs: reserved-channel
/// tokens stay intact, and formatting the decompiled form of every shipped world is idempotent.</summary>
public class FormatterSugarTests {
    private static IEnumerable<string> ExtractDollarTokens(string line) {
        var start = -1;

        for (var i = 0; (i < line.Length); i++) {
            if (
                (line[i] == '$') &&
                (start < 0)
            ) {
                start = i;
            } else if (
                (start >= 0) &&
                !(char.IsLetterOrDigit(c: line[i]) || (line[i] is '_' or '$' or '.' or ':' or '[' or ']' or '-'))
            ) {
                yield return line[start..i];
                start = -1;
            }
        }
        if (start >= 0) {
            yield return line[start..];
        }
    }
    private static string DescribeStatements(DocumentNode document) {
        var builder = new StringBuilder();

        foreach (var statement in document.Statements) {
            switch (statement) {
                case StabilizeGroupNode stabilize:
                    builder.AppendLine(value: $"stabilize {stabilize.Name} statements={stabilize.Statements.Count} maxPasses={(stabilize.MaxPasses is not null)} until={(stabilize.UntilCondition is not null)}");
                    break;

                case WorkflowNode workflow:
                    builder.AppendLine(value: $"workflow {workflow.Name}");
                    foreach (var step in workflow.Steps) {
                        builder.AppendLine(value: $"  {step.Kind} {step.Name} statements={step.Statements.Count} until={(step.UntilCondition is not null)}");
                    }
                    break;

                default:
                    builder.AppendLine(value: statement.GetType().Name);
                    break;
            }
        }

        return builder.ToString();
    }

    [Fact]
    public void DerivedOperandsKeepReservedChannelsInsideCalls() {
        const string Source = "derive legal = maximum($local:actor == turn, minimum($local:suit == led, $local:void))\n";
        var formatted = PuckFormat.Format(source: Source);

        Assert.Equal(Source.Trim(), formatted.Trim());
        Assert.Equal(formatted, PuckFormat.Format(source: formatted));
    }
    [Fact]
    public void FormattingIsIdempotentOnAGateWithMixedChannelsAndKindSuffix() {
        var source = "rule \"r\" {\n    when solitaireFreecell[from] != solitaireFreecell[to] : Int\n}\n";
        var pass1 = PuckFormat.Format(source: source);
        var pass2 = PuckFormat.Format(source: pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
        Assert.Contains(
            actualString: pass1,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "solitaireFreecell[from]"
        );
    }
    [InlineData("when $physics:quiescent == 1 and settleHold < 60")]
    [InlineData("when $upright:placement:$each >= 0.5")]
    [InlineData("when $zones[solitaireFreecell[from]][solitaireFreecell[card]] == 1")]
    [InlineData("transform transfer(from: $zones[solitaireFreecell[from]], to: $zones[solitaireFreecell[to]], selector: Slice, key: $cell:solitaireFreecell:card)")]
    [Theory]
    public void ReservedChannelTokensSurviveFormattingUnsplit(string line) {
        var source = $"rule \"r\" {{\n    {line}\n}}\n";
        var formatted = PuckFormat.Format(source: source);

        // Every `$name:segment` token in the input must reappear in the output with no space inserted around its
        // internal colons — the regression this guards is `$physics:quiescent` splicing into `$physics: quiescent`.
        foreach (var token in ExtractDollarTokens(line: line)) {
            Assert.Contains(
                actualString: formatted,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: token
            );
        }
    }
    [InlineData("""
        stabilize settleBoard maxPasses(32) until board.unstable == 0 {
            rule "collapse" {
                board.unstable = 0
            }
        }
        """)]
    [InlineData("""
        workflow turn {
            step beginTurn {
                combat.active = 1
            }
            repeatStep resolveCombat until combat.active == 0 {
                rule "strike" {
                    combat.active = 0
                }
            }
            step endTurn {
                combat.active = 0
            }
        }
        """)]
    [Theory]
    public void RuntimelessConstructsSurviveAFormatRoundTrip(string body) {
        var source = $"""
            schema: "puck.world.definition.v1"

            {body}

            """;
        var parsed = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.False(
            condition: parsed.Diagnostics.HasErrors,
            userMessage: parsed.Diagnostics.FormatReport(source)
        );

        var formatted = PuckFormat.Format(source: source);

        Assert.Equal(
            actual: PuckFormat.Format(source: formatted),
            expected: formatted
        );
        var reparsed = PuckParser.ParseDocumentWithDiagnostics(formatted);

        Assert.False(
            condition: reparsed.Diagnostics.HasErrors,
            userMessage: reparsed.Diagnostics.FormatReport(formatted)
        );
        Assert.Equal(
            actual: DescribeStatements(document: reparsed.Value!),
            expected: DescribeStatements(document: parsed.Value!)
        );
    }


}
