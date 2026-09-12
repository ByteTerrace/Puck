using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// puck.world.def.v1 rule bodies are straight-line and its gates are comparisons, so the world side of these
// features is a named refusal rather than a lowering.
public class ControlFlowTests {
    private static RuleBlockNode ParseOneRule(string body) {
        var document = PuckParser.ParseDocument($$"""
            schema: "puck.world.def.v1"

            rule "probe" {
            {{body}}
            }
            """);

        return Assert.IsType<RuleBlockNode>(document.Statements[0]);
    }

    private static DiagnosticBag LowerForDiagnostics(string source) {
        var diagnostics = new DiagnosticBag();

        WorldDocumentEmitter.LowerWithDiagnostics(PuckParser.ParseDocument(source), diagnostics: diagnostics, cancellationToken: TestContext.Current.CancellationToken);

        return diagnostics;
    }

    [Fact]
    public void TestIfParsesItsThenAndElseBodies() {
        var rule = ParseOneRule("""
                if score[total] > 8 {
                    score[total] = 0
                } else {
                    score[total] += 1
                }
            """);
        var branch = Assert.IsType<IfStatementNode>(rule.Statements[0]);

        Assert.IsType<ComparisonPredicateNode>(branch.Condition);
        Assert.Single(branch.Then);
        Assert.IsType<SetCellStatementNode>(branch.Then[0]);
        Assert.NotNull(branch.Else);
        Assert.IsType<AddCellStatementNode>(branch.Else[0]);
    }

    [Fact]
    public void TestBareIfCarriesNoElse() {
        var rule = ParseOneRule("""
                if score[total] > 8 {
                    score[total] = 0
                }
            """);

        Assert.Null(Assert.IsType<IfStatementNode>(rule.Statements[0]).Else);
    }

    [Fact]
    public void TestElseIfNestsRatherThanAddingAThirdShape() {
        var rule = ParseOneRule("""
                if score[total] > 8 {
                    score[total] = 0
                } else if score[total] > 4 {
                    score[total] = 1
                } else {
                    score[total] = 2
                }
            """);
        var outer = Assert.IsType<IfStatementNode>(rule.Statements[0]);

        // An `else if` is an Else holding one nested if: a chain of any length has one branch's shape.
        var inner = Assert.IsType<IfStatementNode>(Assert.Single(outer.Else!));

        Assert.NotNull(inner.Else);
        Assert.Single(inner.Else);
    }

    [Fact]
    public void TestBareIfFollowedByAnotherStatementDoesNotSwallowIt() {
        var rule = ParseOneRule("""
                if score[total] > 8 {
                    score[total] = 0
                }
                score[rounds] += 1
            """);

        Assert.Equal(2, rule.Statements.Count);
        Assert.IsType<IfStatementNode>(rule.Statements[0]);
        Assert.IsType<AddCellStatementNode>(rule.Statements[1]);
    }

    [Fact]
    public void TestRepeatCarriesACountAndAnIndexName() {
        var rule = ParseOneRule("""
                repeat 4 as k {
                    score[total] += 1
                }
            """);
        var loop = Assert.IsType<RepeatStatementNode>(rule.Statements[0]);

        Assert.Equal("k", loop.Index);
        Assert.Equal(4L, Assert.IsType<long>(Assert.IsType<LiteralExpressionNode>(loop.Count).Value));
        Assert.Single(loop.Body);
    }

    [Fact]
    public void TestRepeatCountMayBeAConstant() {
        var document = PuckParser.ParseDocument("""
            schema: "puck.world.def.v1"

            let corners = 4

            rule "probe" {
                repeat corners as k {
                    score[total] += 1
                }
            }
            """);
        var rule = Assert.IsType<RuleBlockNode>(document.Statements[1]);
        var loop = Assert.IsType<RepeatStatementNode>(rule.Statements[0]);

        Assert.Equal("corners", Assert.IsType<IdentifierExpressionNode>(loop.Count).Name);
    }

    [Fact]
    public void TestBreakParsesAsItsOwnStatement() {
        var rule = ParseOneRule("""
                repeat 4 as k {
                    break
                }
            """);
        var loop = Assert.IsType<RepeatStatementNode>(rule.Statements[0]);

        Assert.IsType<BreakStatementNode>(Assert.Single(loop.Body));
    }

    [Fact]
    public void TestCallFormGateParsesAsACallPredicate() {
        var rule = ParseOneRule("""
                when key(left, held)
                score[total] += 1
            """);
        var gate = Assert.IsType<WhenStatementNode>(rule.Statements[0]);
        var call = Assert.IsType<CallPredicateNode>(gate.Predicate);

        Assert.Equal("key", call.Call.Name);
        Assert.Equal(2, call.Call.Arguments.Count);
    }

    [Fact]
    public void TestACallOnTheLeftOfAComparisonIsStillAComparison() {
        var rule = ParseOneRule("""
                when minimum(a, b) == 3
                score[total] += 1
            """);
        var gate = Assert.IsType<WhenStatementNode>(rule.Statements[0]);

        // The call-gate scan is speculative precisely so this case rewinds instead of swallowing the comparator.
        var comparison = Assert.IsType<ComparisonPredicateNode>(gate.Predicate);

        Assert.Equal("==", comparison.Comparator);
        Assert.Equal("minimum(a, b)", comparison.LeftText);
    }

    [Fact]
    public void TestWorldLoweringRefusesEachControlFlowStatementByName() {
        foreach (var (body, keyword) in new[] {
            ("if score[total] > 8 {\n        score[total] = 0\n    }", "if"),
            ("repeat 4 as k {\n        score[total] = 0\n    }", "repeat"),
            ("break", "break"),
        }) {
            var diagnostics = LowerForDiagnostics($$"""
                schema: "puck.world.def.v1"

                rule "probe" {
                    {{body}}
                }
                """);

            Assert.Contains(diagnostics, diagnostic =>
                (diagnostic.Code == PuckDiagnosticCodes.UnsupportedControlFlow) && diagnostic.Message.Contains($"'{keyword}'"));
        }
    }

    [Fact]
    public void TestWorldLoweringRefusesACallFormGateByName() {
        var diagnostics = LowerForDiagnostics("""
            schema: "puck.world.def.v1"

            rule "probe" {
                when key(left, held)
                score[total] += 1
            }
            """);

        Assert.Contains(diagnostics, diagnostic =>
            (diagnostic.Code == PuckDiagnosticCodes.UnsupportedCallGate) && diagnostic.Message.Contains("key(...)"));
    }

    [Fact]
    public void TestFormatterRoundTripsAControlFlowBody() {
        const string source = """
            schema: "puck.world.def.v1"

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

        var formatted = PuckFormatter.Format(source);
        var rule = Assert.IsType<RuleBlockNode>(PuckParser.ParseDocument(formatted).Statements[0]);
        var branch = Assert.IsType<IfStatementNode>(rule.Statements[0]);
        var loop = Assert.IsType<RepeatStatementNode>(Assert.Single(branch.Then));

        Assert.IsType<BreakStatementNode>(Assert.Single(loop.Body));
        Assert.NotNull(branch.Else);
    }
}
