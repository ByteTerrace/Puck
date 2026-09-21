using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Lsp;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A described member of a CLOSED construct is honoured by every reader, and a word the description does
/// not carry is refused by name rather than dropped.</summary>
/// <remarks><c>slot</c> is the subject because its modifier admission is the description itself — the emitter
/// admits <see cref="WorldConstructs.ModifiersOf"/> and refuses everything else — so acceptance there is evidence
/// about the row, which it is not on an open-sugar section where any word round-trips. The six readers are the
/// parser, the lowering, the printer, the projection, completion and hover, plus the generated manual.</remarks>
public class ConstructDescriptionProofLawTests {
    private const string Closed = "slot";
    private const string Enclosing = "world";
    private const string Space = """
            spaces {
                space lore {
                    model: "puck-fixture"
                    revision: "1"
                    dimensions: 8
                }
            }
        """;

    private static string Doc(string body) => $"schema: \"puck.world.definition.v1\"\n\n{body}\n";
    // One source per described `slot` modifier, spelling that modifier and nothing else optional.
    private static string SourceFor(string member) => (member switch {
        "space" => Doc(body: $"state {{\n{Space}\n    world {{\n        slot pos space(lore)\n    }}\n}}"),
        _ => Doc(body: $"state {{\n    world {{\n        slot hp = 1 {member}{Arguments(member: member)}\n    }}\n}}"),
    });
    private static string Arguments(string member) => (member switch {
        "advance" => "(perSecond: 1)",
        "bounds" => "(0..9, overflow: Saturate)",
        _ => "()",
    });
    private static JsonObject Compile(string source) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: $"{compilation.Diagnostics.FormatReport(source)}{Environment.NewLine}{source}"
        );

        return compilation.RequireJson();
    }
    private static IReadOnlySet<string> CompletionLabels() {
        var items = new JsonArray();

        WorldConstructLanguageServices.AddCompletions(
            items: items,
            table: WorldConstructs.Table
        );

        return items.Select(selector: static item => (item?["label"]?.ToString() ?? "")).ToHashSet(comparer: StringComparer.Ordinal);
    }
    // The generated manual's section for one construct, which is where its member rows are.
    private static string ManualSection(string keyword) {
        var rendered = WorldConstructs.Table.Render();
        var start = rendered.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: $"\n### `{keyword}`\n"
        );

        Assert.True(
            condition: (start >= 0),
            userMessage: $"the generated manual has no section for '{keyword}'"
        );

        var end = rendered.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: (start + 1),
            value: "\n### "
        );

        return ((end < 0)
            ? rendered[start..]
            : rendered[start..end]
        );
    }

    public static TheoryData<string> Modifiers() => new(values: WorldConstructs.ModifiersOf(enclosing: Enclosing, keyword: Closed).Order(comparer: StringComparer.Ordinal));
    [Fact]
    public void AWordTheDescriptionDoesNotCarryIsRefusedAndTheRefusalNamesWhatItDoes() {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: Doc(body: "state {\n    world {\n        slot hp = 1 freeze(1)\n    }\n}")
        );

        Assert.True(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: "an undescribed modifier compiled, so acceptance on this construct is no evidence about its description"
        );

        var report = compilation.Diagnostics.FormatReport("");

        Assert.Contains(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "'freeze'"
        );

        // The refusal lists the admitted modifiers, and the list is the description's.
        Assert.Contains(
            expectedSubstring: string.Join(
                separator: ", ",
                values: WorldConstructs.ModifiersOf(enclosing: Enclosing, keyword: Closed).Order(comparer: StringComparer.Ordinal)
            ),
            actualString: report,
            comparisonType: StringComparison.Ordinal
        );
    }
    [MemberData(nameof(Modifiers))]
    [Theory]
    public void ADescribedMemberOfAClosedConstructIsHonouredByEveryReader(string name) {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(
            construct: out var construct,
            keyword: Closed
        ));
        Assert.True(condition: construct!.TryGetMember(
            member: out var member,
            name: name
        ));

        var source = SourceFor(member: name);

        // The parser accepts it.
        var document = Compile(source: source);
        var row = document["state"]?["world"]?[0];

        // The lowering consumes it: every key the description names is on the row.
        foreach (var key in member!.DocumentKeys) {
            Assert.True(
                condition: (row?[key] is not null),
                userMessage: $"'{name}' names '{key}', which the compiled row does not carry: {row?.ToJsonString()}"
            );
        }

        // The printer prints it back — as the modifier when the row sugars, as an ordinary property when it falls
        // back — and the projection closes over the print either way.
        var printed = WorldDecompiler.Decompile(root: document);

        Assert.Contains(
            actualString: printed,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: name
        );
        Assert.Equal(
            actual: Compile(source: printed).ToJsonString(),
            expected: document.ToJsonString()
        );

        // Completion offers it, hover describes it, and the generated manual lists it.
        Assert.Contains(
            collection: CompletionLabels(),
            expected: name
        );
        var card = (WorldConstructLanguageServices.Hover(
            enclosing: Closed,
            table: table,
            word: name
        ) ?? "");

        Assert.Contains(
            expectedSubstring: member.Summary,
            actualString: card,
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            actualString: card,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"A `{Closed}` member"
        );
        Assert.Contains(
            expectedSubstring: $"| `{member.Spelling()}`",
            actualString: ManualSection(keyword: Closed),
            comparisonType: StringComparison.Ordinal
        );
    }
}
