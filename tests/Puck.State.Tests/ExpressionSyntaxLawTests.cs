using Xunit;

namespace Puck.State.Tests;

public sealed class ExpressionSyntaxLawTests {
    [Fact]
    public void ParenthesesKeepLiveKeysDistinctFromLiteralKeys() {
        Assert.True(ExpressionSpelling.TryParseSyntax("row[(selected)]", [], out var syntax, out var error), error);
        var index = Assert.IsType<ExpressionSpelling.SourceIndex>(syntax);
        var group = Assert.IsType<ExpressionSpelling.SourceGroup>(index.Index);
        Assert.Equal(new ExpressionSpelling.SourceName("selected"), group.Value);
    }
    [Fact]
    public void SourceTreePreservesQuotedNamesMembersAndIndexedAccess() {
        Assert.True(ExpressionSpelling.TryParseSyntax("`Color.Red` + pool[slot].hp * Color.Red", [], out var syntax, out var error), error);
        var sum = Assert.IsType<ExpressionSpelling.Binary>(syntax);
        Assert.Equal(actual: sum.Operator, expected: "+");
        Assert.Equal(new ExpressionSpelling.SourceName("Color.Red", true), sum.Left);
        var product = Assert.IsType<ExpressionSpelling.Binary>(@object: sum.Right);
        Assert.Equal(actual: product.Operator, expected: "*");
        var field = Assert.IsType<ExpressionSpelling.SourceAccess>(@object: product.Left);
        Assert.Equal("hp", field.Member);
        var index = Assert.IsType<ExpressionSpelling.SourceIndex>(field.Target);
        Assert.Equal(new ExpressionSpelling.SourceName("pool"), index.Target);
        Assert.Equal(new ExpressionSpelling.SourceName("slot"), index.Index);
        Assert.Equal(new ExpressionSpelling.SourceAccess(new ExpressionSpelling.SourceName("Color"), "Red"), product.Right);
    }

    [Fact]
    public void AtomsAreOpaqueEvenWhenTheirTextContainsOperandSyntax() {
        const string Source = "row[$\"name + {x}\"] + 1";
        Assert.True(ExpressionSpelling.TryParseSyntax(Source, [new(4, 13)], out var syntax, out var error), error);
        var sum = Assert.IsType<ExpressionSpelling.Binary>(syntax);
        var index = Assert.IsType<ExpressionSpelling.SourceIndex>(@object: sum.Left);
        Assert.Equal(new ExpressionSpelling.SourceAtom(0), index.Index);
        Assert.Equal(new ExpressionSpelling.Literal(1), sum.Right);
    }

    [Theory]
    [InlineData("->")]
    [InlineData("=>")]
    public void CallsRetainNestedFoldBodiesAndNamedArguments(string arrow) {
        Assert.True(ExpressionSpelling.TryParseSyntax($"sum(hand, card {arrow} max(card, limit)) + count(zone, where: live)", [], out var syntax, out var error), error);
        var sum = Assert.IsType<ExpressionSpelling.Binary>(syntax);
        var fold = Assert.IsType<ExpressionSpelling.SourceCall>(@object: sum.Left);
        var lambda = Assert.IsType<ExpressionSpelling.SourceLambda>(fold.Arguments[1].Value);
        Assert.Equal("card", lambda.Binder);
        Assert.IsType<ExpressionSpelling.SourceCall>(lambda.Body);
        var count = Assert.IsType<ExpressionSpelling.SourceCall>(@object: sum.Right);
        Assert.Equal("where", count.Arguments[1].Name);
    }

    [Fact]
    public void SyntaxRejectsExcessivePostfixDepthAndInvalidAtomSpans() {
        Assert.False(ExpressionSpelling.TryParseSyntax(("row" + string.Concat(values: Enumerable.Repeat(count: (ExpressionSpelling.MaxNesting + 1), element: "[x]"))), [], out _, out _));
        Assert.False(ExpressionSpelling.TryParseSyntax("row", [new(0, 4)], out _, out _));
        Assert.False(ExpressionSpelling.TryParseSyntax("row", [new(0, 2), new(1, 1)], out _, out _));
    }
}
