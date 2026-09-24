using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The qualified-name laws: <see cref="QualifiedName"/> reads a dotted name into its segments and back
/// without changing it, and both grammars' member-access chains spell the same name through it.</summary>
public class QualifiedNameLawTests {
    public static TheoryData<string> Spellings() => new(values: ["score", "box.score", "box.child.score", "a.", ".a", "a..b", ""]);
    [MemberData(nameof(Spellings))]
    [Theory]
    public void ParsingAndPrintingANameIsTheIdentity(string text) {
        var name = QualifiedName.Parse(text: text);

        Assert.Equal(actual: name.ToString(), expected: text);
        Assert.Equal(actual: string.Join(separator: QualifiedName.Separator, values: name.Segments), expected: text);
        Assert.Equal(actual: name.IsQualified, expected: text.Contains(value: QualifiedName.Separator));
        Assert.Equal(actual: name.IsWellFormed, expected: text.Split(separator: '.').All(predicate: static part => (part.Length > 0)));
    }
    [Fact]
    public void AQualifiedNameSplitsIntoHeadTailQualifierAndLast() {
        var name = QualifiedName.Parse(text: "box.child.score");

        Assert.Equal(actual: (name.Head, name.Tail, name.Qualifier, name.Last), expected: ("box", "child.score", "box.child", "score"));
        Assert.Equal(actual: name.Append(member: "max").ToString(), expected: "box.child.score.max");

        var single = QualifiedName.Parse(text: "score");

        Assert.Equal(actual: (single.Head, single.Tail, single.Qualifier, single.Last), expected: ("score", "", "", "score"));
    }
    [InlineData("box.score")]
    [InlineData("box.child.score")]
    [Theory]
    public void BothGrammarsSpellAnAccessChainAsTheSameName(string text) {
        var segments = text.Split(separator: '.');
        ExpressionNode chain = new IdentifierExpressionNode(Name: segments[0]);

        foreach (var member in segments.Skip(count: 1)) {
            chain = new MemberAccessExpressionNode(Member: member, Target: chain);
        }

        Assert.Equal(actual: QualifiedName.From(expression: chain)?.ToString(), expected: text);
        Assert.Equal(actual: QualifiedName.From(expression: PuckParser.ParseExpression(source: text))?.ToString(), expected: text);
        Assert.True(condition: ExpressionSpelling.TryParseSyntax(atoms: [], error: out var error, syntax: out var syntax, text: text), userMessage: error);
        Assert.Equal(actual: QualifiedName.From(syntax: syntax!)?.ToString(), expected: text);
        Assert.Equal(actual: QualifiedName.From(syntax: QualifiedName.Parse(text: text).ToSyntax())?.ToString(), expected: text);
    }
    [Fact]
    public void AChainOverAnythingButANameSpellsNoName() {
        Assert.Null(@object: QualifiedName.From(expression: new MemberAccessExpressionNode(Member: "x", Target: new LiteralExpressionNode(Value: 1L))));
        Assert.Null(@object: QualifiedName.From(syntax: new ExpressionSpelling.SourceAccess(Member: "x", Target: new ExpressionSpelling.SourceName(Name: "row", Quoted: true))));
    }
}
