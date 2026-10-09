using Puck.Cli.Docs;
using Xunit;

namespace Puck.Cli.Source.Tests;

/// <summary>
/// THE LAW: <c>docs citations</c> reads a cited name and the source literal that carries it with one spelling, so a
/// refusal code whose segments carry underscores (<c>world.mutation.activation_mismatch</c>) is cited whole, found whole
/// among the source's literals, and resolves. Only whitespace separates argument text; an attached suffix never
/// resolves by borrowing a shorter name's vocabulary entry.
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
    [InlineData("world.mutation.activation_mismatch/retired")]
    [InlineData("world.mutation.activation_mismatch\\retired")]
    [InlineData("world.mutation.activation_mismatch:443/path")]
    [InlineData("world.mutation.activation_mismatch.1_000")]
    [InlineData("world.mutation.activation_mismatch._retired")]
    [InlineData("world.mutation.activation_mismatch.Retired")]
    [InlineData("world.mutation.activation_mismatch, ")]
    [InlineData("world.mutation.activation_mismatch.")]
    [InlineData("world.mutation.activation_mismatch()")]
    [InlineData("world_mutation.activation_mismatch")]
    [InlineData("1_000.2_000")]
    [InlineData("https://world.mutation.activation_mismatch")]
    [Theory]
    public void ANonNameNeverCitesOrCarriesAPrefix(string text) {
        Assert.Null(@object: First(
            pattern: DocsCitationsCommand.MarkdownToken,
            text: $"`{text}`"
        ));
        Assert.Null(@object: First(
            pattern: DocsCitationsCommand.XmlDocToken,
            text: $"<c>{text}</c>"
        ));
        Assert.Null(@object: First(
            pattern: DocsCitationsCommand.SourceLiteral,
            text: $"\"{text}\""
        ));
        Assert.Null(@object: First(
            pattern: DocsCitationsCommand.Registration,
            text: $"    name: \"{text}\","
        ));
    }
    [InlineData(Code)]
    [InlineData("world.mutation.ingress_refused")]
    [InlineData("world.transport.operation_metadata_unsupported")]
    [InlineData("world.render-scale")]
    [InlineData("audio.masterGain")]
    [InlineData("world.segment_1.next_segment_2")]
    [Theory]
    public void EveryVocabularyReadsANameWithTheSameSpelling(string name) {
        Assert.Equal(
            actual: First(
                pattern: DocsCitationsCommand.MarkdownToken,
                text: $"`{name} argument`),"
            ),
            expected: name
        );
        Assert.Equal(
            actual: First(
                pattern: DocsCitationsCommand.XmlDocToken,
                text: $"<c>{name}\targument</c>),"
            ),
            expected: name
        );
        Assert.Equal(
            actual: First(
                pattern: DocsCitationsCommand.SourceLiteral,
                text: $"\"{name}\""
            ),
            expected: name
        );
        Assert.Equal(
            actual: First(
                pattern: DocsCitationsCommand.Registration,
                text: $"    name: \"{name}\","
            ),
            expected: name
        );
    }
}
