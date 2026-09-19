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
                table board : Int {
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
            expectedSubstring: "rule \"settleBoard_collapse\""
        );
        Assert.DoesNotContain(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "turn_beginTurn"
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
