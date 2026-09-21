using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// puck.world.definition.v1 rule bodies are straight-line and its gates are comparisons, so the world side of these
// features is a named refusal rather than a lowering.
public class ControlFlowTests {
    private static DiagnosticBag LowerForDiagnostics(string source) =>
        WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        ).Diagnostics;
    private static RuleBlockNode ParseOneRule(string body) {
        var document = PuckParser.ParseDocument($$"""
            schema: "puck.world.definition.v1"

            rule "probe" {
            {{body}}
            }
            """);

        return Assert.IsType<RuleBlockNode>(@object: document.Statements[0]);
    }

    [Fact]
    public void TestACallOnTheLeftOfAComparisonIsStillAComparison() {
        var rule = ParseOneRule(body: """
                when minimum(a, b) == 3
                score[total] += 1
            """);
        var gate = Assert.IsType<WhenStatementNode>(@object: rule.Statements[0]);

        // The call-gate scan is speculative precisely so this case rewinds instead of swallowing the comparator.
        var comparison = Assert.IsType<ComparisonPredicateNode>(@object: gate.Predicate);

        Assert.Equal(
            "==",
            comparison.Comparator
        );
        Assert.Equal(
            "minimum(a, b)",
            comparison.Left.Text
        );
    }
    [Fact]
    public void TestBareIfCarriesNoElse() {
        var rule = ParseOneRule(body: """
                if score[total] > 8 {
                    score[total] = 0
                }
            """);

        Assert.Null(@object: Assert.IsType<IfStatementNode>(@object: rule.Statements[0]).Else);
    }
    [Fact]
    public void TestBareIfFollowedByAnotherStatementDoesNotSwallowIt() {
        var rule = ParseOneRule(body: """
                if score[total] > 8 {
                    score[total] = 0
                }
                score[rounds] += 1
            """);

        Assert.Equal(
            2,
            rule.Statements.Count
        );
        Assert.IsType<IfStatementNode>(@object: rule.Statements[0]);
        Assert.IsType<AddCellStatementNode>(@object: rule.Statements[1]);
    }
    [Fact]
    public void TestBreakParsesAsItsOwnStatement() {
        var rule = ParseOneRule(body: """
                repeat 4 as k {
                    break
                }
            """);
        var loop = Assert.IsType<RepeatStatementNode>(@object: rule.Statements[0]);

        Assert.IsType<BreakStatementNode>(@object: Assert.Single(collection: loop.Body));
    }
    [Fact]
    public void TestCallFormGateParsesAsACallPredicate() {
        var rule = ParseOneRule(body: """
                when key(left, held)
                score[total] += 1
            """);
        var gate = Assert.IsType<WhenStatementNode>(@object: rule.Statements[0]);
        var call = Assert.IsType<CallPredicateNode>(@object: gate.Predicate);

        Assert.Equal(
            "key",
            call.Call.Name
        );
        Assert.Equal(
            2,
            call.Call.Arguments.Count
        );
    }
    [Fact]
    public void TestElseIfNestsRatherThanAddingAThirdShape() {
        var rule = ParseOneRule(body: """
                if score[total] > 8 {
                    score[total] = 0
                } else if score[total] > 4 {
                    score[total] = 1
                } else {
                    score[total] = 2
                }
            """);
        var outer = Assert.IsType<IfStatementNode>(@object: rule.Statements[0]);

        // An `else if` is an Else holding one nested if: a chain of any length has one branch's shape.
        var inner = Assert.IsType<IfStatementNode>(@object: Assert.Single(collection: outer.Else!));

        Assert.NotNull(@object: inner.Else);
        Assert.Single(collection: inner.Else);
    }
    [Fact]
    public void TestFormatterRoundTripsAControlFlowBody() {
        const string Source = """
            schema: "puck.world.definition.v1"

            rule "probe" {
                if score[total] > 8 {
                    repeat 4 as k {
                        break
                    }
                } else {
                    score[total] += 1
                }
            }
            """;

        var formatted = PuckFormat.Format(Source);
        var rule = Assert.IsType<RuleBlockNode>(@object: PuckParser.ParseDocument(formatted).Statements[0]);
        var branch = Assert.IsType<IfStatementNode>(@object: rule.Statements[0]);
        var loop = Assert.IsType<RepeatStatementNode>(@object: Assert.Single(collection: branch.Then));

        Assert.IsType<BreakStatementNode>(@object: Assert.Single(collection: loop.Body));
        Assert.NotNull(@object: branch.Else);
    }
    [Fact]
    public void TestIfParsesItsThenAndElseBodies() {
        var rule = ParseOneRule(body: """
                if score[total] > 8 {
                    score[total] = 0
                } else {
                    score[total] += 1
                }
            """);
        var branch = Assert.IsType<IfStatementNode>(@object: rule.Statements[0]);

        Assert.IsType<ComparisonPredicateNode>(@object: branch.Condition);
        Assert.Single(collection: branch.Then);
        Assert.IsType<SetCellStatementNode>(@object: branch.Then[0]);
        Assert.NotNull(@object: branch.Else);
        Assert.IsType<AddCellStatementNode>(@object: branch.Else[0]);
    }
    [Fact]
    public void TestRepeatCarriesACountAndAnIndexName() {
        var rule = ParseOneRule(body: """
                repeat 4 as k {
                    score[total] += 1
                }
            """);
        var loop = Assert.IsType<RepeatStatementNode>(@object: rule.Statements[0]);

        Assert.Equal(
            "k",
            loop.Index
        );
        Assert.Equal(
            4L,
            Assert.IsType<long>(@object: Assert.IsType<LiteralExpressionNode>(@object: loop.Count).Value)
        );
        Assert.Single(collection: loop.Body);
    }
    [Fact]
    public void TestRepeatCountMayBeAConstant() {
        var document = PuckParser.ParseDocument("""
            schema: "puck.world.definition.v1"

            let corners = 4

            rule "probe" {
                repeat corners as k {
                    score[total] += 1
                }
            }
            """);
        var rule = Assert.IsType<RuleBlockNode>(@object: document.Statements[1]);
        var loop = Assert.IsType<RepeatStatementNode>(@object: rule.Statements[0]);

        Assert.Equal(
            "corners",
            Assert.IsType<IdentifierExpressionNode>(@object: loop.Count).Name
        );
    }
    [Fact]
    public void TestWorldLoweringRefusesACallFormGateByName() {
        var diagnostics = LowerForDiagnostics(source: """
            schema: "puck.world.definition.v1"

            rule "probe" {
                when key(left, held)
                score[total] += 1
            }
            """);

        Assert.Contains(
            collection: diagnostics,
            filter: diagnostic =>
            ((diagnostic.Code == PuckDiagnosticCodes.UnsupportedCallGate) && diagnostic.Message.Contains(value: "key(...)"))
        );
    }
    [Fact]
    // `if` is absent: world rules lower it onto the conditional effect.
    public void TestWorldLoweringRefusesEachControlFlowStatementByName() {
        foreach (var (body, keyword) in new[] {
            ("repeat 4 as k {\n        score[total] = 0\n    }", "repeat"),
            ("break", "break"),
        }) {
            var diagnostics = LowerForDiagnostics(source: $$"""
                schema: "puck.world.definition.v1"

                rule "probe" {
                    {{body}}
                }
                """);

            Assert.Contains(
                collection: diagnostics,
                filter: diagnostic =>
                ((diagnostic.Code == PuckDiagnosticCodes.UnsupportedControlFlow) && diagnostic.Message.Contains(value: $"'{keyword}'"))
            );
        }
    }
}
