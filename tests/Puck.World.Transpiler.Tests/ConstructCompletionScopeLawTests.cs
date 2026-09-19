using Puck.World.Transpiler.Lsp;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Completion inside a construct's body offers what the description admits there and nothing else, while
/// completion at document statement position offers the whole vocabulary.</summary>
/// <remarks>Driven through the real language server, so the cursor-to-enclosing-construct resolution is part of
/// what is proved. A table-sourced item is told from the hand-written extras by its own detail line, which is the
/// only mechanical separator: the extras carry labels that collide with described keywords
/// (<c>rules</c> names a construct as well as the array its rows sit in).</remarks>
public class ConstructCompletionScopeLawTests {
    private const string HostBody = """
        schema: "puck.world.definition.v1"

        host {

        }
        """;
    private const string Root = "schema: \"puck.world.definition.v1\"\n\n";
    // The cursor's line inside the `host { }` body, 0-based.
    private const int HostBodyLine = 3;

    private static IReadOnlyList<string> ConstructLabels(IEnumerable<OfferedCompletion> offered) => Sorted(labels: offered
        .Where(predicate: static item => item.Detail.Contains(
        comparisonType: StringComparison.Ordinal,
        value: " construct: lowers to "
    ))
        .Select(selector: static item => item.Label));
    private static IReadOnlyList<string> MemberLabels(IEnumerable<OfferedCompletion> offered) => Sorted(labels: offered
        .Where(predicate: static item => item.Detail.Contains(
        comparisonType: StringComparison.Ordinal,
        value: "` member: "
    ))
        .Select(selector: static item => item.Label));
    // Every member name completion can offer: a header or body member is written as part of the construct's own
    // statement, so it never stands as a label of its own.
    private static IReadOnlyList<string> OfferableMemberNames(WorldConstruct construct) => Sorted(labels: construct.Members
        .Where(predicate: static member => (member.Position is not (WorldMemberPosition.Body or WorldMemberPosition.Header)))
        .Select(selector: static member => member.Name));
    private static IReadOnlyList<string> Sorted(IEnumerable<string> labels) =>
        [.. labels.Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)];
    private static string Text(IEnumerable<string> labels) => string.Join(
        separator: ", ",
        values: labels
    );

    [Fact]
    public void DocumentStatementPositionOffersTheWholeVocabulary() {
        var table = WorldConstructs.Table;
        var offered = LanguageServerCompletions.At(
            character: 0,
            line: 2,
            source: Root
        );
        var constructs = ConstructLabels(offered: offered);
        var members = MemberLabels(offered: offered);

        Assert.Equal(
            actual: Text(labels: table.Keywords.Except(second: constructs, comparer: StringComparer.Ordinal)),
            expected: ""
        );

        // A member whose name is already a keyword's takes the keyword's item, so the union is what carries it.
        var labelled = constructs.Concat(second: members).ToHashSet(comparer: StringComparer.Ordinal);
        var described = Sorted(labels: table.Constructs.SelectMany(selector: OfferableMemberNames));

        Assert.Equal(
            actual: Text(labels: described.Except(second: labelled, comparer: StringComparer.Ordinal)),
            expected: ""
        );
    }
    [Fact]
    public void AConstructsBodyOffersOnlyWhatIsDescribedThere() {
        var table = WorldConstructs.Table;

        // The position resolves to the enclosing construct, so a failure below is about scoping rather than about
        // the cursor landing nowhere.
        Assert.Equal(
            actual: WorldConstructLanguageServices.ConstructAt(
                offset: HostBody.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n').Take(count: HostBodyLine).Sum(selector: static part => (part.Length + 1)),
                table: table,
                text: HostBody
            ),
            expected: "host"
        );
        Assert.True(condition: table.TryGet(
            construct: out var host,
            keyword: "host"
        ));

        var offered = LanguageServerCompletions.At(
            character: 0,
            line: HostBodyLine,
            source: HostBody
        );

        Assert.NotEmpty(collection: offered);
        Assert.Equal(
            actual: Text(labels: ConstructLabels(offered: offered)),
            expected: Text(labels: Sorted(labels: table.Inside(enclosing: "host").Select(selector: static construct => construct.Keyword)))
        );
        Assert.Equal(
            actual: Text(labels: MemberLabels(offered: offered)),
            expected: Text(labels: OfferableMemberNames(construct: host!))
        );

        // The exclusion is what this position carries: the document's own constructs are offered where they are
        // legal and not here.
        Assert.Contains(
            collection: ConstructLabels(offered: LanguageServerCompletions.At(
                character: 0,
                line: 2,
                source: Root
            )),
            expected: "state"
        );
        Assert.DoesNotContain(
            collection: ConstructLabels(offered: offered),
            expected: "state"
        );
    }
}
