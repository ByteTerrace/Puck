using Xunit;

namespace Puck.State.Tests;

/// <summary>The two infix front ends for the boolean-closed algebras: printing a node and parsing that text back
/// gives the same node, over every case each vocabulary carries, and the operators bind in the order the spelling
/// documents.</summary>
public class AlgebraSpellingLawTests {
    private static PatternNode Reparse(PatternNode node) {
        Assert.True(
            condition: PatternSpelling.TryParse(
                error: out var error,
                node: out var parsed,
                text: PatternSpelling.Print(node: node)
            ),
            userMessage: $"'{PatternSpelling.Print(node: node)}' {error}"
        );

        return parsed!;
    }
    private static CellSetExpression Reparse(CellSetExpression expression) {
        Assert.True(
            condition: CellSetSpelling.TryParse(
                error: out var error,
                expression: out var parsed,
                text: CellSetSpelling.Print(expression: expression)
            ),
            userMessage: $"'{CellSetSpelling.Print(expression: expression)}' {error}"
        );

        return parsed!;
    }
    private static PatternNode Parse(string text) {
        Assert.True(
            condition: PatternSpelling.TryParse(
                error: out var error,
                node: out var parsed,
                text: text
            ),
            userMessage: $"'{text}' {error}"
        );

        return parsed!;
    }
    private static CellSetExpression ParseSet(string text) {
        Assert.True(
            condition: CellSetSpelling.TryParse(
                error: out var error,
                expression: out var parsed,
                text: text
            ),
            userMessage: $"'{text}' {error}"
        );

        return parsed!;
    }
    private static CellSetExpression Zone(string row) => new CellSetExpression.Zone(
        High: 1,
        Low: 1,
        Row: CellName.Parse(candidate: row)
    );

    public static TheoryData<PatternNode> PatternCases() {
        var face = new PatternNode.Symbol(Name: "face");
        var next = new PatternNode.Symbol(Name: "next");

        return new TheoryData<PatternNode>(values: ((PatternNode[])[
            face,
            new PatternNode.AnySymbol(),
            new PatternNode.Except(Name: "face"),
            new PatternNode.Nothing(),
            new PatternNode.None(),
            new PatternNode.Symbol(Name: "any"),
            new PatternNode.Sequence(Items: [face, new PatternNode.Star(Item: next)]),
            new PatternNode.Choice(Items: [face, next, new PatternNode.AnySymbol()]),
            new PatternNode.Both(Items: [new PatternNode.Plus(Item: face), new PatternNode.Complement(Item: next)]),
            new PatternNode.Complement(Item: new PatternNode.Sequence(Items: [face, next])),
            new PatternNode.Optional(Item: new PatternNode.Choice(Items: [face, next])),
            new PatternNode.Repeat(
                Item: face,
                Max: 12,
                Min: 12
            ),
            new PatternNode.Repeat(
                Item: new PatternNode.Choice(Items: [face, next]),
                Max: 4,
                Min: 2
            ),
            new PatternNode.Sequence(Items: [
                new PatternNode.Star(Item: new PatternNode.AnySymbol()),
                new PatternNode.Symbol(Name: "king"),
                new PatternNode.Repeat(
                    Item: next,
                    Max: 12,
                    Min: 12
                ),
            ]),
        ]));
    }
    public static TheoryData<CellSetExpression> CellSetCases() {
        var tableau = new CellSetExpression.Family(
            High: 13,
            Low: 1,
            Name: CellName.Parse(candidate: "tableau")
        );
        var board = new CellSetExpression.Board(
            High: 1,
            Low: -1,
            Row: CellName.Parse(candidate: "board")
        );

        return new TheoryData<CellSetExpression>(values: ((CellSetExpression[])[
            tableau,
            board,
            Zone(row: "pile"),
            new CellSetExpression.Everything(),
            new CellSetExpression.Nothing(),
            new CellSetExpression.Any(Items: [tableau, board]),
            new CellSetExpression.Both(Items: [tableau, new CellSetExpression.Complement(Item: board)]),
            new CellSetExpression.Complement(Item: new CellSetExpression.Any(Items: [tableau, board])),
            new CellSetExpression.Any(Items: [
                new CellSetExpression.Both(Items: [tableau, board]),
                Zone(row: "pile"),
            ]),
        ]));
    }
    [MemberData(memberName: nameof(PatternCases))]
    [Theory]
    public void APatternPrintsAndParsesBackToItself(PatternNode node) => Assert.Equal(
        PatternSpelling.Print(node: node),
        PatternSpelling.Print(node: Reparse(node: node))
    );
    [MemberData(memberName: nameof(CellSetCases))]
    [Theory]
    public void ACellSetPrintsAndParsesBackToItself(CellSetExpression expression) => Assert.Equal(
        CellSetSpelling.Print(expression: expression),
        CellSetSpelling.Print(expression: Reparse(expression: expression))
    );
    [Fact]
    public void ChoiceBindsLoosestAndIntersectionBindsTighterThanSequence() {
        var choice = Assert.IsType<PatternNode.Choice>(@object: Parse(text: "a b | c & d e"));

        Assert.IsType<PatternNode.Sequence>(@object: choice.Items[0]);

        var both = Assert.IsType<PatternNode.Both>(@object: choice.Items[1]);

        Assert.IsType<PatternNode.Symbol>(@object: both.Items[0]);
        Assert.IsType<PatternNode.Sequence>(@object: both.Items[1]);
    }
    [Fact]
    public void ARepetitionBindsTighterThanAComplement() {
        var complement = Assert.IsType<PatternNode.Complement>(@object: Parse(text: "~a*"));

        Assert.IsType<PatternNode.Star>(@object: complement.Item);
    }
    [Fact]
    public void AQuotedSymbolNamesTheLetterAndTheBareWordNamesEveryToken() {
        Assert.Equal(
            "any",
            Assert.IsType<PatternNode.Symbol>(@object: Parse(text: "\"any\"")).Name
        );
        Assert.IsType<PatternNode.AnySymbol>(@object: Parse(text: "any"));
        Assert.Equal(
            "\"any\"",
            PatternSpelling.Print(node: new PatternNode.Symbol(Name: "any"))
        );
    }
    // A quoted name escapes its backslashes as well as its quotes, so the name read back is the name printed.
    [Theory]
    [InlineData("a\\b")]
    [InlineData("a\\")]
    [InlineData("\\\"")]
    [InlineData("say \"hi\"")]
    public void AQuotedSymbolNameReadsBackAsItWasPrinted(string name) => Assert.Equal(
        name,
        Assert.IsType<PatternNode.Symbol>(@object: Parse(text: PatternSpelling.Print(node: new PatternNode.Symbol(Name: name)))).Name
    );
    [Fact]
    public void ASymbolWithNoNameHasNoSpellingAndAnEmptyQuotedNameSaysWhy() {
        Assert.False(condition: PatternSpelling.TryPrint(
            node: new PatternNode.Symbol(Name: string.Empty),
            text: out _
        ));
        Assert.False(condition: PatternSpelling.TryParse(
            error: out var error,
            node: out _,
            text: "\"\""
        ));
        Assert.NotEmpty(collection: error);
    }
    // Every refusal names its reason, whichever step of the grammar refuses: an empty quoted row name included.
    [Theory]
    [InlineData("board(\"\", 0..1)")]
    [InlineData("board(\"pile, 0..1)")]
    [InlineData("board(, 0..1)")]
    [InlineData("board(pile 0..1)")]
    [InlineData("board(pile, x..1)")]
    [InlineData("board(pile, 0.1)")]
    [InlineData("board(pile, 0..)")]
    [InlineData("board(pile, 0..1")]
    [InlineData("board pile")]
    [InlineData("tile(pile, 0..1)")]
    [InlineData("(all")]
    [InlineData("all none")]
    [InlineData("all |")]
    [InlineData("~")]
    [InlineData(" ")]
    public void AMalformedCellSetIsRefusedWithAReason(string text) {
        Assert.False(condition: CellSetSpelling.TryParse(
            error: out var error,
            expression: out var expression,
            text: text
        ));
        Assert.Null(@object: expression);
        Assert.NotEmpty(collection: error);
    }
    [Fact]
    public void AnEmptyQuotedRowNameSaysWhy() {
        Assert.False(condition: CellSetSpelling.TryParse(
            error: out var error,
            expression: out _,
            text: "board(\"\", 0..1)"
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "row name between the quotes"
        );
    }
    // A cell-set row name escapes its quotes and backslashes the way a pattern symbol does, through one quoting.
    [Fact]
    public void AQuotedRowNameReadsBackAsItWasPrinted() {
        var expression = new CellSetExpression.Board(
            High: 1,
            Low: 0,
            Row: CellName.Parse(candidate: "pile one")
        );
        var text = CellSetSpelling.Print(expression: expression);

        Assert.Equal(
            actual: text,
            expected: "board(\"pile one\", 0..1)"
        );
        Assert.Equal(
            text,
            CellSetSpelling.Print(expression: Reparse(expression: expression))
        );
    }
    [Fact]
    public void AnNaryNodeCarryingOneItemHasNoSpelling() {
        Assert.False(condition: PatternSpelling.TryPrint(
            node: new PatternNode.Sequence(Items: [new PatternNode.AnySymbol()]),
            text: out _
        ));
        Assert.False(condition: CellSetSpelling.TryPrint(
            expression: new CellSetExpression.Any(Items: [Zone(row: "pile")]),
            text: out _
        ));
    }
    [Fact]
    public void UnionBindsLoosestAndComplementBindsTightest() {
        var any = Assert.IsType<CellSetExpression.Any>(@object: ParseSet(text: "zone(a, 1..1) | ~zone(b, 1..1) & all"));

        Assert.IsType<CellSetExpression.Zone>(@object: any.Items[0]);

        var both = Assert.IsType<CellSetExpression.Both>(@object: any.Items[1]);

        Assert.IsType<CellSetExpression.Complement>(@object: both.Items[0]);
        Assert.IsType<CellSetExpression.Everything>(@object: both.Items[1]);
    }
    [Fact]
    public void AMalformedTextIsRefusedByName() {
        Assert.False(condition: PatternSpelling.TryParse(
            error: out var patternError,
            node: out _,
            text: "a |"
        ));
        Assert.NotEmpty(collection: patternError);
        Assert.False(condition: CellSetSpelling.TryParse(
            error: out var setError,
            expression: out _,
            text: "zone(a, 4..1)"
        ));
        Assert.Contains(
            actualString: setError,
            expectedSubstring: "least first"
        );
    }
}
