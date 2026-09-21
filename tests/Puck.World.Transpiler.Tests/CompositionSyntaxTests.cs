using Puck.Transpiler.Ast;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Rewriting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class CompositionSyntaxTests {
    private sealed class Rename : PuckSyntaxRewriter {
        protected override ExpressionNode RewriteExpression(ExpressionNode expression) => (base.RewriteExpression(expression: expression) switch {
            IdentifierExpressionNode { Name: "island" } identifier => (identifier with { Name = "atoll" }),
            var other => other,
        });
    }

    [Fact]
    public void CompositionFormsRoundTripAndTraverseExpressions() {
        var source = """
            world $"island-{i}" = terrain(seed: i)
            border island.arch(1), neighbour.sky {
              width: 4
              center [1, 2, 3]
            }
            door island.under(island), neighbour.sky
            """;
        var document = PuckParser.ParseDocument(source);

        var world = Assert.IsType<WorldDeclarationNode>(@object: document.Statements[0]);

        Assert.IsType<InterpolatedStringNode>(world.Name);
        Assert.Equal("terrain", world.Module.Name);
        var border = Assert.IsType<WorldLinkNode>(@object: document.Statements[1]);

        Assert.Equal("border", border.Kind);
        Assert.Equal(2, border.Properties.Count);
        var door = Assert.IsType<WorldLinkNode>(@object: document.Statements[2]);

        Assert.Equal("door", door.Kind);
        Assert.Empty(door.Properties);

        var printed = PuckPrinter.Print(document);
        var reparsed = PuckParser.ParseDocument(printed);
        var rewritten = new Rename().Rewrite(document: reparsed);
        var rewrittenBorder = Assert.IsType<WorldLinkNode>(@object: rewritten.Statements[1]);
        var left = Assert.IsType<CallExpressionNode>(rewrittenBorder.Left);

        Assert.Equal("island.arch", left.Name);
        var rewrittenDoor = Assert.IsType<WorldLinkNode>(@object: rewritten.Statements[2]);
        var under = Assert.IsType<CallExpressionNode>(rewrittenDoor.Left);

        Assert.Equal("atoll", Assert.IsType<IdentifierExpressionNode>(@object: under.Arguments[0].Value).Name);
    }
    [Fact]
    public void AssetPathIsAnExpressionAndRoundTrips() {
        var expression = PuckParser.ParseExpression(source: "asset \"worlds/island.puck\"");
        var asset = Assert.IsType<AssetExpressionNode>(@object: expression);

        Assert.Equal("worlds/island.puck", asset.Path);
        Assert.Equal("asset \"worlds/island.puck\"", PuckPrinter.PrintExpression(asset));
    }
    [Fact]
    public void QualifiedCallsDoNotReassociateMemberAccess() {
        Assert.Equal("island.arch", Assert.IsType<IdentifierExpressionNode>(@object: PuckParser.ParseExpression(source: "island.arch")).Name);
        var call = Assert.IsType<CallExpressionNode>(@object: PuckParser.ParseExpression(source: "island.arch(1)"));

        Assert.Equal("island.arch", call.Name);
        var trailing = Assert.IsType<MemberAccessExpressionNode>(@object: PuckParser.ParseExpression(source: "island.arch(1).edge"));

        Assert.Equal("edge", trailing.Member);
        Assert.IsType<CallExpressionNode>(@object: trailing.Target);
    }
    [InlineData("world island terrain()")]
    [InlineData("world island = terrain")]
    [InlineData("border island.sky neighbour.sky { width: 1 }")]
    [InlineData("door island.sky, neighbour.sky { width: 1 }")]
    [Theory]
    public void MalformedCompositionFormsAreRefused(string source) {
        Assert.Throws<PuckParseException>(testCode: () => PuckParser.ParseDocument(source));
    }
    [Fact]
    public void ExistingWorldBlockRemainsABlock() {
        var document = PuckParser.ParseDocument("state { world { slot score = 0 } }");
        var state = Assert.IsType<BlockNode>(@object: document.Statements[0]);

        Assert.IsType<BlockNode>(@object: state.Statements[0]);
    }
    [InlineData("host { world: concat(\"moth\", \"-courtyard\") }")]
    [InlineData("host { world [1, 2] }")]
    [InlineData("host { border: 2 }")]
    [InlineData("host { door { enabled: true } }")]
    [Theory]
    public void CompositionKeywordsRemainAvailableToGenericFields(string source) {
        var document = PuckParser.ParseDocument(source);
        var host = Assert.IsType<BlockNode>(@object: document.Statements[0]);

        Assert.Single(collection: host.Statements);
    }
}
