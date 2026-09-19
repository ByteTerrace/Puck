using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Lsp;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The readers read the description: the parser's embedded-language test, the language server's
/// completion and hover, and the manual's vocabulary tables all come from the one construct table.</summary>
public class ConstructReaderLawTests {
    private const string GeneratedDocument = "docs/reference/world-vocabulary.md";

    private static string FindRepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(path: Path.Combine(
                path1: directory,
                path2: "Puck.slnx"
            ))) {
                return directory;
            }
            directory = Path.GetDirectoryName(path: directory);
        }

        throw new DirectoryNotFoundException(message: "Could not locate the repository root from the test runner.");
    }
    private static IReadOnlySet<string> CompletionLabels(WorldConstructTable table) {
        var items = new JsonArray();

        WorldConstructLanguageServices.AddCompletions(
            items: items,
            table: table
        );

        return items.Select(selector: static item => (item?["label"]?.ToString() ?? "")).ToHashSet(comparer: StringComparer.Ordinal);
    }

    public static TheoryData<string> Described() => new(values: WorldConstructs.Table.Keywords);

    [Fact]
    public void CompletionOffersEveryDescribedConstruct() {
        var labels = CompletionLabels(table: WorldConstructs.Table);

        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: WorldConstructs.Table.Keywords.Where(predicate: keyword => !labels.Contains(item: keyword))
            ),
            expected: ""
        );
    }
    [Fact]
    public void TheGeneratedManualTableOnDiskMatchesTheTable() {
        var path = Path.Combine(
            path1: FindRepositoryRoot(),
            path2: GeneratedDocument
        );

        Assert.True(
            condition: File.Exists(path: path),
            userMessage: $"{GeneratedDocument} is missing; run `puck vocabulary` to write it."
        );

        var expected = WorldConstructs.Table.Render().Split(separator: '\n');
        var actual = File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n").Split(separator: '\n');
        var line = 0;

        while (
            (line < expected.Length) &&
            (line < actual.Length) &&
            string.Equals(
            a: expected[line],
            b: actual[line],
            comparisonType: StringComparison.Ordinal
        )
        ) {
            line++;
        }

        Assert.Equal(
            actual: ((line < actual.Length)
                ? $"{GeneratedDocument}:{line + 1}: {actual[line]}"
                : ""),
            expected: ((line < expected.Length)
                ? $"{GeneratedDocument}:{line + 1}: {expected[line]}"
                : "")
        );
    }
    [Fact]
    public void TheVocabularyDelegatesItsEmbeddedLanguagesToTheTable() {
        foreach (var keyword in WorldConstructs.Table.Keywords.Concat(second: ["mysql", "python", ""])) {
            Assert.Equal(
                actual: WorldDocumentVocabulary.Instance.IsEmbeddedLanguage(identifier: keyword),
                expected: WorldConstructs.Table.IsEmbeddedLanguage(identifier: keyword)
            );
        }
        Assert.True(condition: WorldDocumentVocabulary.Instance.IsEmbeddedLanguage(identifier: "SQL"));
    }
    [MemberData(nameof(Described))]
    [Theory]
    public void HoverDescribesADescribedConstructAndItsMembers(string keyword) {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(
            construct: out var construct,
            keyword: keyword
        ));

        var card = WorldConstructLanguageServices.Hover(
            table: table,
            word: keyword
        );

        Assert.NotNull(@object: card);
        Assert.Contains(
            expectedSubstring: construct!.DocumentMember,
            actualString: card!,
            comparisonType: StringComparison.Ordinal
        );

        foreach (var member in construct.Members) {
            var memberCard = WorldConstructLanguageServices.Hover(
                enclosing: keyword,
                table: table,
                word: member.Name
            );

            Assert.NotNull(@object: memberCard);
            Assert.Contains(
                expectedSubstring: $"A `{keyword}` member",
                actualString: memberCard!,
                comparisonType: StringComparison.Ordinal
            );
        }
    }
}
