using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Xunit;
using RuleGroupShape = Puck.State.Rules.RuleGroupShape;
using RuleGroupStepPolicy = Puck.State.Rules.RuleGroupStepPolicy;

namespace Puck.World.Transpiler.Tests;

/// <summary>The <c>stabilize</c>/<c>workflow</c> pair against the <c>ruleGroups</c> document member: the group and
/// the rules it claims are one construct in source, so the decompiler writes them back as one and the source it
/// writes compiles to the document it read.</summary>
public class RuleGroupSugarTests {
    [InlineData("stabilize turn undo({ rows [\"board\"] depth: 8 }) { rule move { board.unstable = 0 } }")]
    [InlineData("workflow turn undo({ rows [\"board\"] depth: 8 }) { step move { board.unstable = 0 } }")]
    [Theory]
    public void UndoDeclarationAndRewindEffectRoundTrip(string group) {
        var source = (("schema: \"puck.world.definition.v1\"\nstate { world { table board { unstable = 1 } } }\n" + group) + "\nrule undoMove { rewindGroup(turn) }");
        var document = Compile(source: source);

        Assert.Equal(8L, document["ruleGroups"]![0]!["undo"]!["depth"]!.GetValue<long>());
        Assert.Equal("board", document["ruleGroups"]![0]!["undo"]!["rows"]![0]!.GetValue<string>());
        var printed = WorldDecompiler.Decompile(root: document);

        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: " undo(");
        Assert.True(condition: JsonNode.DeepEquals(node1: document, node2: Compile(source: printed)), userMessage: printed);
        var formatted = PuckFormat.Format(source: source);

        Assert.True(condition: JsonNode.DeepEquals(node1: document, node2: Compile(source: formatted)), userMessage: formatted);
    }
    [Fact]
    public void DuplicateUndoModifierRefuses() {
        var result = WorldCompiler.Compile(source: "stabilize turn undo({ rows [\"board\"] depth: 1 }) undo({ rows [\"board\"] depth: 2 }) { rule move { board.x = 1 } }", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: result.Diagnostics.HasErrors);
    }

    private static JsonObject Compile(string source) {
        var lowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.False(
            condition: lowered.Diagnostics.HasErrors,
            userMessage: lowered.Diagnostics.FormatReport(source)
        );

        return lowered.Json!;
    }

    private const string Source = """
        schema: "puck.world.definition.v1"

        state {
            world {
                table board {
                    unstable = 1
                }
            }
        }

        stabilize settleBoard maxPasses(32) until board.unstable == 0 {
            rule "collapse" {
                board.unstable = 0
            }
        }

        workflow turn {
            step beginTurn {
                board.unstable = 1
            }

            step endTurn skip {
                board.unstable = 0
            }
        }
        """;

    [Fact]
    public void ARuleGroupAndItsMembersDecompileAsOneConstructAndRecompileToTheSameDocument() {
        var document = Compile(source: Source);
        var decompiled = WorldDecompiler.Decompile(root: document);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "stabilize settleBoard maxPasses(32) until "
        );
        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "step endTurn skip {"
        );

        // A claimed rule belongs to its group's block, never to a `rule` statement of its own.
        Assert.DoesNotContain(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "rule \"settleBoard$collapse\""
        );
        Assert.DoesNotContain(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "turn$beginTurn"
        );
        Assert.Equal(
            expected: document.ToJsonString(),
            actual: Compile(source: decompiled).ToJsonString()
        );
    }
    [Fact]
    public void ALoweredRuleGroupCompilesThroughTheNewRuleCompiler() {
        var document = Compile(source: Source);
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: document.ToJsonString()));

        var (rules, groups) = WorldFactsCompiler.CompileDocument(definition: definition);

        Assert.Equal(
            actual: rules.Length,
            expected: 3
        );
        Assert.Equal(
            actual: groups.Length,
            expected: 2
        );

        var fixpoint = Assert.Single(collection: groups, predicate: static group => (group.Shape == RuleGroupShape.Fixpoint));

        Assert.Equal(
            actual: fixpoint.Name,
            expected: "settleBoard"
        );
        Assert.Equal(
            actual: fixpoint.Passes,
            expected: 32
        );
        Assert.NotEmpty(collection: fixpoint.Trigger);
        Assert.Equal(
            actual: fixpoint.TerminalStep,
            expected: -1
        );

        var staged = Assert.Single(collection: groups, predicate: static group => (group.Shape == RuleGroupShape.Staged));

        Assert.Equal(
            actual: staged.TerminalStep,
            expected: 1
        );
        Assert.Equal(
            actual: staged.Policies,
            expected: [RuleGroupStepPolicy.Stall, RuleGroupStepPolicy.Skip]
        );

        // Every member the groups claim is one of the compiled rules, and no rule belongs to two groups.
        Assert.Equal(
            actual: groups.SelectMany(selector: static group => group.Members).Distinct().Count(),
            expected: 3
        );
    }
}
