using System.Text;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A completion for a described construct — or for the array spelling of one — inserts source the
/// language accepts.</summary>
/// <remarks>Driven through the real language server, over the completions that insert a whole array: a container
/// written with a <c>:</c> is PUCK040, which the server cannot see because it never parses what it offers. A
/// completion whose last placeholder is where the author still types a required value — a <c>set</c>'s
/// expression, a <c>rule</c>'s body — cannot be parsed as inserted and is not answerable here.</remarks>
public class ConstructCompletionSnippetLawTests {
    private const string Root = "schema: \"puck.world.definition.v1\"\n\n";

    // The LSP snippet grammar's placeholders, reduced to what an editor leaves once the author tabs through taking
    // every default: `${1:name}` -> `name`, `${1|a,b|}` -> `a`, `${1:}` and `$0` -> nothing.
    private static string Filled(string snippet) {
        var output = new StringBuilder();
        var index = 0;

        while (index < snippet.Length) {
            if (snippet[index] != '$') {
                _ = output.Append(value: snippet[index++]);

                continue;
            }
            index++;
            if ((index < snippet.Length) && (snippet[index] == '{')) {
                var close = snippet.IndexOf(
                    startIndex: index,
                    value: '}'
                );

                if (close < 0) {
                    break;
                }

                var body = snippet[(index + 1)..close];
                var colon = body.IndexOf(value: ':');
                var bar = body.IndexOf(value: '|');

                if (colon >= 0) {
                    _ = output.Append(value: body[(colon + 1)..]);
                } else if (bar >= 0) {
                    _ = output.Append(value: body[(bar + 1)..].TrimEnd(trimChars: ['|']).Split(separator: ',')[0]);
                }
                index = (close + 1);

                continue;
            }
            while ((index < snippet.Length) && char.IsDigit(c: snippet[index])) {
                index++;
            }
        }

        return output.ToString();
    }
    // Every label-and-snippet pair the real language server offers at document statement position.
    private static IReadOnlyList<(string Label, string Snippet)> OfferedAtRoot() => RootOffers.Value;

    private static readonly Lazy<IReadOnlyList<(string Label, string Snippet)>> RootOffers = new(valueFactory: static () => [.. LanguageServerClient
        .CompletionsAt(
        character: 0,
        line: 2,
        source: Root
    )
        .Select(selector: static item => (
            Label: item.Label,
            Snippet: item.InsertText
        ))
        .Where(predicate: static entry => (entry.Snippet.Length > 0))]);

    // What the PARSER refuses about a source, which is the level a snippet is answerable at: a snippet's last
    // placeholder is where the author still has to type, so an empty rule body or an empty `match:` is not its
    // defect. A container spelled with a `:` (PUCK040) is, and the parser is what draws it.
    private static string Refusals(string source) {
        var diagnostics = new DiagnosticBag();

        _ = PuckParser.ParseDocumentWithDiagnostics(
            diagnostics: diagnostics,
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        return string.Join(
            separator: " | ",
            values: diagnostics
                .Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error))
                .Select(selector: static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
        );
    }

    public static TheoryData<string, string> ArraySpellings() {
        var table = WorldConstructs.Table;
        var data = new TheoryData<string, string>();

        foreach (var (label, snippet) in OfferedAtRoot()) {
            if (table.TryGetByArraySpelling(
                construct: out _,
                word: label
            )) {
                data.Add(
                    p1: label,
                    p2: snippet
                );
            }
        }

        return data;
    }
    [Fact]
    public void TheLanguageServerOffersTheArraySpellingOfARowConstruct() {
        var table = WorldConstructs.Table;
        var offered = OfferedAtRoot()
            .Select(selector: static entry => entry.Label)
            .Where(predicate: label => table.TryGetByArraySpelling(
            construct: out _,
            word: label
        ))
            .Distinct(comparer: StringComparer.Ordinal)
            .Order(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: offered
            ),
            expected: "addons, materials"
        );
    }
    [MemberData(nameof(ArraySpellings))]
    [Theory]
    public void AnArraySpellingCompletionInsertsSourceTheLanguageAccepts(string label, string snippet) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{Filled(snippet: snippet)}\n";

        Assert.Equal(
            actual: $"{label}: {Refusals(source: source)}",
            expected: $"{label}: "
        );
    }
    // `rules` names a construct as well as the array its rows sit in, so the array-spelling lookup cannot single
    // its completion out; it is taken by the shape of what it inserts instead.
    [Fact]
    public void TheRulesArrayCompletionInsertsSourceTheLanguageAccepts() {
        var offered = OfferedAtRoot()
            .Where(predicate: static entry => entry.Snippet.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "rules ["
        ))
            .ToArray();

        Assert.True(
            condition: (offered.Length == 1),
            userMessage: $"the language server offers {offered.Length} completions inserting a `rules [` array"
        );
        Assert.Equal(
            actual: Refusals(source: $"schema: \"puck.world.definition.v1\"\n\n{Filled(snippet: offered[0].Snippet)}\n"),
            expected: ""
        );
    }
}
