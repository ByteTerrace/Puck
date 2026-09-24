using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Rewriting;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class OperandTreeLawTests {
    private const string Prefix = """
        schema: "puck.world.definition.v1"
        let amount = 3
        state {
            enum Choice { First, Second }
            world {
                slot result = 0
                slot selected = 0
                table values { amount = 8 }
                table scores[0, 2..3]
            }
        }

        """;

    private static JsonObject Compile(string source) {
        var result = WorldCompiler.Compile(source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: result.Diagnostics.HasErrors, userMessage: result.Diagnostics.FormatReport(source));
        return result.RequireJson();
    }

    [InlineData("amount + Choice.Second")]
    [InlineData("values[amount] + amount")]
    [InlineData("values[(selected)] + amount")]
    [InlineData("values[$\"amount\"] + $\"{amount}\"")]
    [Theory]
    public void SugarCallsAndBlockMembersBindTheSameTree(string expression) {
        var sugar = Compile(source: (Prefix + $"rule r {{ result = {expression} }}"))["rules"]![0]!["effects"]![0]!["expression"];
        var call = Compile(source: (Prefix + $"rule r {{ setState(state: result, expression: {expression}) }}"))["rules"]![0]!["effects"]![0]!["expression"];
        var block = Compile(source: (Prefix + $$"""
            rules [{ name: "r"
                effects [{ $type: setState
                    state: result
                    expression: {{expression}}
                }]
            }]
            """))["rules"]![0]!["effects"]![0]!["expression"];

        Assert.NotNull(@object: sugar);
        Assert.True(condition: JsonNode.DeepEquals(node1: sugar, node2: call), userMessage: call?.ToJsonString());
        Assert.True(condition: JsonNode.DeepEquals(node1: sugar, node2: block), userMessage: block?.ToJsonString());
    }
    [Fact]
    public void FamilyMemberKeysSurviveBindingAndGapsAreRefused() {
        var effect = Compile(source: (Prefix + "rule r { setState(state: scores[2], key: amount, expression: amount) }"))["rules"]![0]!["effects"]![0]!;

        Assert.Equal("scores2", effect["state"]!.GetValue<string>());
        Assert.Equal("amount", effect["key"]!.GetValue<string>());
        var missing = WorldCompiler.Compile((Prefix + "rule r { result = scores[1] }"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(collection: missing.Diagnostics, filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.FamilyIndexOutOfBounds));
    }
    [Fact]
    public void ARewriteReachesParsedNamesAndPreservesLiteralKeys() {
        var source = (Prefix + "rule r { result = values[amount] + amount }");
        var tree = PuckParser.ParseDocument(source, vocabulary: WorldDocumentVocabulary.Instance);
        var rewritten = new RenameOperand().Rewrite(document: tree);
        var printed = Puck.Transpiler.Formatting.PuckPrinter.Print(document: rewritten);

        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "values[amount]");
        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "+ 7");
        var result = Compile(source: printed)["rules"]![0]!["effects"]![0]!["expression"]!;

        Assert.Equal(7m, result["instructions"]![1]!["value"]!.GetValue<decimal>());
    }
    [Fact]
    public void LintSeesSugarReferencesButNotLiteralKeys() {
        var used = new DiagnosticBag();

        PuckLinter.Lint(PuckParser.ParseDocument((Prefix + "rule r { result = amount }"), vocabulary: WorldDocumentVocabulary.Instance), used);
        Assert.DoesNotContain(collection: used, filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.LintUnusedLet));
        var unused = new DiagnosticBag();

        PuckLinter.Lint(PuckParser.ParseDocument((Prefix + "rule r { result = values[amount] }"), vocabulary: WorldDocumentVocabulary.Instance), unused);
        Assert.Contains(collection: unused, filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.LintUnusedLet));
    }
    [InlineData("clamp(1, 2)")]
    [InlineData("min(1, 2, 3)")]
    [InlineData("1 +")]
    [Theory]
    public void InvalidOperandsAreRefusedBeforeDocumentPublication(string expression) {
        var result = WorldCompiler.Compile((Prefix + $"rule r {{ result = {expression} }}"), cancellationToken: TestContext.Current.CancellationToken);

        // The parser reports the refusal, and lowering the operand does not report it again.
        _ = Assert.Single(collection: result.Diagnostics, predicate: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.OperandParse));
    }
    [Fact]
    public void BoundObjectMembersAndArrayIndicesAreResolvedStructurally() {
        var source = (Prefix + "let settings = { count: 5 }\nlet counts = [2, 7]\nrule r { result = settings.count + counts[1] }");
        var expression = Compile(source: source)["rules"]![0]!["effects"]![0]!["expression"]!;

        Assert.Equal(5m, expression["instructions"]![0]!["value"]!.GetValue<decimal>());
        Assert.Equal(7m, expression["instructions"]![1]!["value"]!.GetValue<decimal>());
    }
    [Fact]
    public void AnImportAliasReachesBareSugarOperandsWithoutRenamingLiteralKeys() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-tree-import-");

        try {
            File.WriteAllText(Path.Combine(path1: directory.FullName, path2: "library.puck"), """
                let amount = 5
                module emit() {
                    state { world { slot result = 0
                        table values { amount = 8 }
                    } }
                    rule r { result = values[amount] + amount }
                }
                """);
            var path = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: "schema: \"puck.world.definition.v1\"\nimport \"library.puck\" as lib\nuse lib.emit as first()\n", path: path);
            var compilation = WorldCompiler.CompileFile(path, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
            var expression = compilation.RequireJson()["rules"]![0]!["effects"]![0]!["expression"]!;

            Assert.Equal("amount", expression["instructions"]![0]!["key"]!.GetValue<string>());
            Assert.Equal(5m, expression["instructions"]![1]!["value"]!.GetValue<decimal>());
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void ATextOnlyRewriteCannotLeaveStaleOperandSyntax() {
        var tree = PuckParser.ParseDocument((Prefix + "rule r { result = amount }"), vocabulary: WorldDocumentVocabulary.Instance);

        Assert.Throws<PuckRewriteException>(testCode: () => new StaleTextRewrite().Rewrite(document: tree));
    }
    [Fact]
    public void AProjectedOutOfRangeNumberProducesARefusal() {
        var operand = PuckParser.CreateOperand(
            expression: new Puck.Transpiler.Ast.LiteralExpressionNode(1e100), form: DocumentValueForm.Expression);

        Assert.Null(@object: operand.Syntax);
        Assert.Contains("decimal range", operand.SyntaxError, StringComparison.Ordinal);
    }

    private sealed class StaleTextRewrite : PuckSyntaxRewriter {
        protected override Puck.Transpiler.Ast.ExpressionNode RewriteExpression(Puck.Transpiler.Ast.ExpressionNode expression) =>
            ((expression is Puck.Transpiler.Ast.OperandExpressionNode operand)
                ? operand with { Text = "7" } : base.RewriteExpression(expression: expression));
    }
    private sealed class RenameOperand : PuckSyntaxRewriter {
        protected override ExpressionSpelling.SyntaxNode RewriteOperandSyntax(ExpressionSpelling.SyntaxNode node, DocumentValueForm form) =>
            (((node is ExpressionSpelling.SourceName { Name: "amount", Quoted: false }) && (form == DocumentValueForm.Expression))
                ? new ExpressionSpelling.Literal(Value: 7) : base.RewriteOperandSyntax(form: form, node: node));
    }
}
