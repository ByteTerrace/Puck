using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a spelling nests at most <see cref="ExpressionSpelling.MaxNesting"/> deep along
/// every path the parser recurses on, and one level past that is a refusal naming the ceiling rather than a walk
/// the machine stack has to survive. A spelling at the ceiling parses.</summary>
public sealed class ExpressionNestingLawTests {
    private static string Spell(string shape, int depth) => shape switch {
        "parentheses" => ((new string(
            c: '(',
            count: depth
        ) + "1") + new string(
            c: ')',
            count: depth
        )),
        "prefix operators" => (new string(
            c: '-',
            count: depth
        ) + "x"),
        "a run of binary operators" => ("1" + string.Concat(values: Enumerable.Repeat(
            count: depth,
            element: "+1"
        ))),
        "a chain of ternaries" => (string.Concat(values: Enumerable.Repeat(
            count: depth,
            element: "a?1:"
        )) + "0"),
        "nested indices" => ((string.Concat(values: Enumerable.Repeat(
            count: depth,
            element: "r["
        )) + "k") + new string(
            c: ']',
            count: depth
        )),
        "nested arguments" => ((string.Concat(values: Enumerable.Repeat(
            count: depth,
            element: "sign("
        )) + "1") + new string(
            c: ')',
            count: depth
        )),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(shape)),
    };

    public static TheoryData<string> Shapes() => new(
        "parentheses",
        "prefix operators",
        "a run of binary operators",
        "a chain of ternaries",
        "nested indices",
        "nested arguments"
    );
    [MemberData(nameof(Shapes))]
    [Theory]
    public void ASpellingNestedPastTheCeilingIsRefusedByName(string shape) {
        var text = Spell(
            depth: (ExpressionSpelling.MaxNesting + 40),
            shape: shape
        );

        Assert.True(condition: (text.Length <= ExpressionSpelling.MaxLength));
        Assert.False(condition: ExpressionSpelling.TryParse(
            error: out var error,
            program: out _,
            text: text
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: $"nests more than {ExpressionSpelling.MaxNesting} deep"
        );
    }
    [MemberData(nameof(Shapes))]
    [Theory]
    public void ASpellingWellInsideTheCeilingParses(string shape) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                program: out _,
                text: Spell(
                    depth: (ExpressionSpelling.MaxNesting / 2),
                    shape: shape
                )
            ),
            userMessage: error
        );
    }
    [Fact]
    public void TheDepthIsSpentAndReturnedSoSiblingsDoNotAddUp() {
        // Many shallow groups side by side nest no deeper than one of them.
        var text = string.Join(
            separator: "+",
            values: Enumerable.Repeat(
                count: 40,
                element: "((((1))))"
            )
        );

        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                program: out _,
                text: text
            ),
            userMessage: error
        );
    }
}
