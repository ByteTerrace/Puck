using Puck.Cli.Docs;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// THE LAW: <c>docs citations</c> reads a cited name and the source literal that carries it with one spelling, so a
/// refusal code whose segments carry underscores (<c>world.mutation.activation_mismatch</c>) is cited whole, found whole
/// among the source's literals, and resolves; a code span's trailing argument text is still not part of the name.
/// </summary>
public sealed class DocsCitationsTokenLawTests {
    private const string Code = "world.mutation.activation_mismatch";

    private static string? First(System.Text.RegularExpressions.Regex pattern, string text) => ((pattern.Match(input: text) is { Success: true } match)
        ? match.Groups[1].Value
        : null);

    [Fact]
    public void ARefusalCodeIsCitedAndCarriedWhole() {
        Assert.Equal(
            actual: First(
                pattern: DocsCitationsCommand.MarkdownToken,
                text: $"refused on mismatch as `{Code}`),"
            ),
            expected: Code
        );
        Assert.Equal(
            actual: First(
                pattern: DocsCitationsCommand.XmlDocToken,
                text: $"/// (<c>{Code}</c>), before applying anything"
            ),
            expected: Code
        );
        Assert.Equal(
            actual: First(
                pattern: DocsCitationsCommand.SourceLiteral,
                text: $"public const string ActivationMismatchCode = \"{Code}\";"
            ),
            expected: Code
        );
    }
    [Fact]
    public void ACodeSpansArgumentTextIsNotPartOfTheName() =>
        Assert.Equal(
            actual: First(
                pattern: DocsCitationsCommand.MarkdownToken,
                text: "`world.row.set views.seatRig`"
            ),
            expected: "world.row.set"
        );
}
