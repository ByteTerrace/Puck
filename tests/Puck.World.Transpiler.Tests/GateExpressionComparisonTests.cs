using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A <c>when</c> gate admits a whole expression on either side of its comparator, so the coalescing and
/// absence vocabulary (<c>??</c>, <c>isAbsent</c>) is spellable where a zone endpoint is read. A parenthesized span
/// reads as a nested gate first and as an expression operand only when the gate reading does not close, so every
/// gate that parsed before parses the same way.</summary>
public class GateExpressionComparisonTests {
    private static ComparisonPredicateNode FirstGate(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";

        var (document, diagnostics) = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(source)
        );

        var rule = Assert.IsType<RuleBlockNode>(@object: document!.Statements[0]);
        var when = Assert.IsType<WhenStatementNode>(@object: rule.Statements[0]);

        return Assert.IsType<ComparisonPredicateNode>(@object: when.Predicate);
    }

    [Fact]
    public void AndChainAdmitsACoalescedOperandBesideOrdinaryComparisons() {
        var source = """
            schema: "puck.world.definition.v1"

            rule "r" {
                when table[status] == 1 and (face[zone(pile, last)] ?? 1) == 0 and table[busy] == 0
                table[busy] = 1
            }
            """;

        var (document, diagnostics) = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(source)
        );

        var rule = Assert.IsType<RuleBlockNode>(@object: document!.Statements[0]);
        var when = Assert.IsType<WhenStatementNode>(@object: rule.Statements[0]);
        var and = Assert.IsType<AndPredicateNode>(@object: when.Predicate);

        Assert.Equal(
            3,
            and.Operands.Count
        );

        var middle = Assert.IsType<ComparisonPredicateNode>(@object: and.Operands[1]);

        Assert.Equal(
            "(face[zone(pile, last)] ?? 1)",
            middle.Left.Text
        );
    }
    [Fact]
    public void AParenthesizedGateStillReadsAsAGate() {
        var source = """
            schema: "puck.world.definition.v1"

            rule "r" {
                when (table[status] == 1 or table[status] == 2) and table[busy] == 0
                table[busy] = 1
            }
            """;

        var (document, diagnostics) = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(source)
        );

        var rule = Assert.IsType<RuleBlockNode>(@object: document!.Statements[0]);
        var when = Assert.IsType<WhenStatementNode>(@object: rule.Statements[0]);
        var and = Assert.IsType<AndPredicateNode>(@object: when.Predicate);

        Assert.IsType<OrPredicateNode>(@object: and.Operands[0]);
    }
    [Fact]
    public void ACoalescedEndpointReadLowersToCompareValue() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            rule "r" {
                when (face[zone(pile, last)] ?? 1) == 0
                table[busy] = 1
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport()
        );

        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);
        var when = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonObject>(@object: rules[0])["gate"]);

        Assert.Equal(
            "compareValue",
            when["$type"]!.ToString()
        );

        var instructions = Assert.IsType<JsonArray>(@object: when["left"]!["instructions"]);

        Assert.Equal(
            ["Operand", "Constant", "Coalesce"],
            instructions.Select(selector: instruction => instruction!["op"]!.ToString())
        );
    }
    [Fact]
    public void AnAbsenceTestIsSpellableAsAGate() {
        var gate = FirstGate(body: """
            rule "r" {
                when isAbsent(face[zone(pile, last)]) == 1
                table[busy] = 1
            }
            """);

        Assert.Equal(
            "isAbsent(face[zone(pile, last)])",
            gate.Left.Text
        );
    }
    [Fact]
    public void AnExpressionOperandIsAdmittedOnTheRightToo() {
        var gate = FirstGate(body: """
            rule "r" {
                when table[busy] == (face[zone(pile, last)] ?? 1)
                table[busy] = 1
            }
            """);

        Assert.Equal(
            "(face[zone(pile, last)] ?? 1)",
            gate.Right.Text
        );
    }
    [Fact]
    public void AGenuineSyntaxErrorInsideParensStillReportsTheGateReading() {
        var source = """
            schema: "puck.world.definition.v1"

            rule "r" {
                when (table[status] == and)
                table[busy] = 1
            }
            """;

        var (_, diagnostics) = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.Contains(
            collection: diagnostics,
            filter: d => (d.Code == "PUCK001")
        );
    }
}
