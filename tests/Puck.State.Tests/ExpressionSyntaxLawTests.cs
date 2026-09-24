using Xunit;

namespace Puck.State.Tests;

public sealed class ExpressionSyntaxLawTests {
    [Fact]
    public void ParenthesesKeepLiveKeysDistinctFromLiteralKeys() {
        Assert.True(condition: ExpressionSpelling.TryParseSyntax(atoms: [], error: out var error, syntax: out var syntax, text: "row[(selected)]"), userMessage: error);
        var index = Assert.IsType<ExpressionSpelling.SourceIndex>(@object: syntax);
        var group = Assert.IsType<ExpressionSpelling.SourceGroup>(@object: index.Index);

        Assert.Equal(new ExpressionSpelling.SourceName("selected"), group.Value);
    }
    [Fact]
    public void SourceTreePreservesQuotedNamesMembersAndIndexedAccess() {
        Assert.True(condition: ExpressionSpelling.TryParseSyntax(atoms: [], error: out var error, syntax: out var syntax, text: "`Color.Red` + pool[slot].hp * Color.Red"), userMessage: error);
        var sum = Assert.IsType<ExpressionSpelling.Binary>(@object: syntax);

        Assert.Equal(actual: sum.Operator, expected: "+");
        Assert.Equal(new ExpressionSpelling.SourceName(Name: "Color.Red", Quoted: true), sum.Left);
        var product = Assert.IsType<ExpressionSpelling.Binary>(@object: sum.Right);

        Assert.Equal(actual: product.Operator, expected: "*");
        var field = Assert.IsType<ExpressionSpelling.SourceAccess>(@object: product.Left);

        Assert.Equal("hp", field.Member);
        var index = Assert.IsType<ExpressionSpelling.SourceIndex>(@object: field.Target);

        Assert.Equal(new ExpressionSpelling.SourceName("pool"), index.Target);
        Assert.Equal(new ExpressionSpelling.SourceName("slot"), index.Index);
        Assert.Equal(new ExpressionSpelling.SourceAccess(new ExpressionSpelling.SourceName("Color"), "Red"), product.Right);
    }
    [Fact]
    public void AtomsAreOpaqueEvenWhenTheirTextContainsOperandSyntax() {
        const string Source = "row[$\"name + {x}\"] + 1";

        Assert.True(condition: ExpressionSpelling.TryParseSyntax(Source, [new(Length: 13, Start: 4)], out var syntax, out var error), userMessage: error);
        var sum = Assert.IsType<ExpressionSpelling.Binary>(@object: syntax);
        var index = Assert.IsType<ExpressionSpelling.SourceIndex>(@object: sum.Left);

        Assert.Equal(new ExpressionSpelling.SourceAtom(Index: 0), index.Index);
        Assert.Equal(new ExpressionSpelling.Literal(Value: 1), sum.Right);
    }
    [InlineData("->")]
    [InlineData("=>")]
    [Theory]
    public void CallsRetainNestedFoldBodiesAndNamedArguments(string arrow) {
        Assert.True(condition: ExpressionSpelling.TryParseSyntax(atoms: [], error: out var error, syntax: out var syntax, text: $"sum(hand, card {arrow} max(card, limit)) + count(zone, where: live)"), userMessage: error);
        var sum = Assert.IsType<ExpressionSpelling.Binary>(@object: syntax);
        var fold = Assert.IsType<ExpressionSpelling.SourceCall>(@object: sum.Left);
        var lambda = Assert.IsType<ExpressionSpelling.SourceLambda>(@object: fold.Arguments[1].Value);

        Assert.Equal("card", lambda.Binder);
        Assert.IsType<ExpressionSpelling.SourceCall>(@object: lambda.Body);
        var count = Assert.IsType<ExpressionSpelling.SourceCall>(@object: sum.Right);

        Assert.Equal("where", count.Arguments[1].Name);
    }
    [Fact]
    public void SyntaxRejectsExcessivePostfixDepthAndInvalidAtomSpans() {
        Assert.False(condition: ExpressionSpelling.TryParseSyntax(("row" + string.Concat(values: Enumerable.Repeat(count: (ExpressionSpelling.MaxNesting + 1), element: "[x]"))), [], out _, out _));
        Assert.False(condition: ExpressionSpelling.TryParseSyntax("row", [new(Length: 4, Start: 0)], out _, out _));
        Assert.False(condition: ExpressionSpelling.TryParseSyntax("row", [new(Length: 2, Start: 0), new(Length: 1, Start: 1)], out _, out _));
    }
}
