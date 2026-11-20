using System.Text.Json;
using Puck.State.Rules;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Laws for the closed author walls: inline test commands and bounded malformed-test recovery, rule local key
/// cost parity with inline expressions, <c>with</c> non-object refusal, <c>capacity(auto)</c> empty-table refusal, and
/// list-<c>for</c> format round-trip.</summary>
public class AuthorWallsLawTests {
    [Fact]
    public void InlineWhenConsoleCommandAcrossMultipleTestsCompilesCleanly() {
        const string Source = """
            schema: "puck.world.definition.v1"
            documentId: "same"
            state {
              world {
                table value { "0" = 7 }
                table marker { "0" = 7 }
              }
            }
            test "pristine" {
              when { seat1: world.state.cell.set marker 0 7 }
              expect { value[0] >= 7 }
            }
            test "changed" {
              given { value[0] = 11 }
              when { seat1: world.state.cell.set value 0 99 }
              expect {
                value[0] == 99
                marker[0] == 7
              }
            }
            test "pristine again" {
              when { seat1: world.state.cell.set marker 0 7 }
              expect { value[0] >= 7 }
            }
            """;

        var result = WorldCompileSession.Shared.Compile(cancellationToken: TestContext.Current.CancellationToken, source: Source);

        Assert.False(condition: result.Diagnostics.HasErrors, userMessage: result.Diagnostics.FormatReport(sourceText: Source));
        Assert.Equal(expected: 3, actual: result.TestWorlds.Count);
    }

    [Fact]
    public void MalformedNestedTestBlockWithNullDiagnosticsDoesNotHang() {
        const string Source = """
            schema: "puck.world.definition.v1"
            test "nested" {
              when {
                worldA {
                  worldB {
                    value[0] >= 7
                  }
                }
              }
              expect { value[0] == 7 }
            }
            """;

        // Parsing without a diagnostic bag exercises the null-diagnostics skipNested recovery path that previously spun.
        var parsed = PuckParser.ParseDocumentWithDiagnostics(source: Source, vocabulary: WorldDocumentVocabulary.Instance);

        Assert.NotNull(@object: parsed.Value);
    }

    [Fact]
    public void RuleLocalKeyMatchesInlineExpressionCost() {
        const string Source = """
            schema: "puck.world.definition.v1"
            documentId: "local-cost"

            state {
              world {
                slot a = 0
                table cards capacity(8) { "0" = 0, "1" = 1 }
                slot out = 0
              }
            }

            rule withLocal {
              local k = a + 1
              when cards[k] == 1
              out = 1
            }

            rule inlineExpr {
              when cards[a + 1] == 1
              out = 1
            }
            """;

        var compiled = WorldCompileSession.Shared.Compile(cancellationToken: TestContext.Current.CancellationToken, source: Source);

        Assert.False(condition: compiled.Diagnostics.HasErrors, userMessage: compiled.Diagnostics.FormatReport(sourceText: Source));

        var keyChannel = compiled.Json!["rules"]![0]!["gate"]!["key"]!["channel"]!.GetValue<string>();

        Assert.Equal(expected: "local", actual: keyChannel);

        var definition = JsonSerializer.Deserialize<WorldDefinition>(
            json: compiled.Json.ToJsonString(),
            options: WorldJsonContext.Default.Options
        )!;
        var context = WorldFactsCompiler.Context(definition: definition);
        var withLocal = RuleCompiler.Compile(context: context, rule: definition.Rules![0]);
        var inlineExpr = RuleCompiler.Compile(context: context, rule: definition.Rules[1]);

        Assert.Equal(expected: inlineExpr.CostBreakdown(context: context), actual: withLocal.CostBreakdown(context: context));
    }

    [Fact]
    public void EmptyTableCapacityAutoIsRefused() {
        const string Source = """
            schema: "puck.world.definition.v1"
            documentId: "empty-auto"

            state {
              world {
                table empty capacity(auto) { }
              }
            }
            """;

        var compiled = WorldCompileSession.Shared.Compile(cancellationToken: TestContext.Current.CancellationToken, source: Source);

        Assert.True(condition: compiled.Diagnostics.HasErrors);
        Assert.Contains(
            actualString: compiled.Diagnostics.FormatReport(sourceText: Source),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "capacity(auto)"
        );
    }

    [Fact]
    public void WithNonObjectOverrideIsRefused() {
        const string Source = """
            schema: "puck.world.definition.v1"
            documentId: "bad-with"

            let baseObj = { a: 1 }
            let notAnObject = 42
            host: baseObj with notAnObject
            """;

        var compiled = WorldCompileSession.Shared.Compile(cancellationToken: TestContext.Current.CancellationToken, source: Source);

        Assert.True(condition: compiled.Diagnostics.HasErrors);
        Assert.Contains(
            actualString: compiled.Diagnostics.FormatReport(sourceText: Source),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "'with'"
        );
    }

    [Fact]
    public void ForInListAndWithExpressionFormatRoundTrip() {
        const string Source = """
            schema: "puck.world.definition.v1"
            documentId: "fmt"

            let defaults = { yawDegrees: 0 }
            let items = [{ id: "a", position [1, 0, 0] }]

            spawnPoints [
              for raw in items {
                let item = defaults with raw
                item
              }
            ]
            """;

        var parsed = PuckParser.ParseDocumentWithDiagnostics(source: Source, vocabulary: WorldDocumentVocabulary.Instance);

        Assert.False(condition: parsed.Diagnostics.HasErrors, userMessage: parsed.Diagnostics.FormatReport(sourceText: Source));

        var printedOnce = PuckPrinter.Print(document: PuckSyntaxTrivia.Attach(document: parsed.Value!, source: Source));
        var reparsed = PuckParser.ParseDocumentWithDiagnostics(source: printedOnce, vocabulary: WorldDocumentVocabulary.Instance);

        Assert.False(condition: reparsed.Diagnostics.HasErrors, userMessage: reparsed.Diagnostics.FormatReport(sourceText: printedOnce));

        var printedTwice = PuckPrinter.Print(document: PuckSyntaxTrivia.Attach(document: reparsed.Value!, source: printedOnce));

        Assert.Equal(expected: printedOnce, actual: printedTwice);
    }
}
