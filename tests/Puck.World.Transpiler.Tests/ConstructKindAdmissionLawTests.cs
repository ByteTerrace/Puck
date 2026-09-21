using Puck.State;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A member whose admission turns on the row's kind is admitted on exactly the kinds the description names,
/// and the refusal the lowering draws names those kinds rather than a list of its own.</summary>
/// <remarks>The subjects are enumerated from the table, so a kind-conditional member the description gains with no
/// probe here fails by name. Each subject is driven twice: once on a kind the facet admits, once on a kind the
/// construct's own admitted kinds carry that the facet does not. A row's kind is inferred rather than annotated
/// (see WorldDocumentEmitter's kind inference), so each probe reaches its target kind through a value or cell of
/// that kind rather than through a header word. <c>space</c> is excluded from the subjects below: a
/// <c>space(...)</c> modifier is itself what infers Vector, so a probe carrying it can never also carry a different
/// inferred kind for the facet to be refused on.</remarks>
public class ConstructKindAdmissionLawTests {
    private const string Space = """
                spaces {
                    space lore {
                        model: "puck-fixture"
                        revision: "1"
                        dimensions: 8
                    }
                }
        """;

    private static readonly string[] GridKinds = ["Int", "Bool"];
    private static readonly string[] RowKinds = Enum.GetNames<CellKind>();

    private static string FullKindsFor(string keyword) => string.Join(
        separator: ", ",
        values: ((keyword == "grid") ? GridKinds : RowKinds)
    );
    private static string[] FullKindChoicesFor(string keyword) => ((keyword == "grid") ? GridKinds : RowKinds);
    private static string Declaration(string keyword, string member, WorldMemberPosition position, string kind) => ((keyword, member, position) switch {
        ("grid", "bounds", _) => $"grid probe dimensions(width: 2, depth: 2) bounds(0..9, overflow: Saturate) {{\n            \"0\" = {Value(kind: kind)}\n        }}",
        ("table", "advance", WorldMemberPosition.Cell) => $"table probe {{\n            a = {Value(kind: kind)} advance(perSecond: 1)\n        }}",
        ("table", "advance", _) => $"table probe advance(perSecond: 1) {{\n            a = {Value(kind: kind)}\n        }}",
        ("slot", "advance", _) => $"slot probe = {Value(kind: kind)} advance(perSecond: 1)",
        ("table", "bounds", _) => $"table probe bounds(0..9, overflow: Saturate) {{\n            a = {Value(kind: kind)}\n        }}",
        ("slot", "bounds", _) => $"slot probe = {Value(kind: kind)} bounds(0..9, overflow: Saturate)",
        ("table", "embeds", _) => $"table probe embeds(probeVectors, space: lore) {{\n            a = {Value(kind: kind)}\n        }}",
        _ => "",
    });
    // The one embeddable literal a probe ever writes ("x"), locked so the `embeds` facet's admitted case compiles
    // without a live embedding call.
    private static EmbeddingLock CreateProbeLock() {
        var lockFile = new EmbeddingLock();
        var space = new EmbeddingLockSpace(
            dimensions: 8,
            model: "puck-fixture",
            revision: "1"
        );
        var hash = EmbeddingLock.ComputeTextHash(text: "x");

        space.Entries[hash] = new EmbeddingLockEntry(
            Text: "x",
            Vector: "fwAAAAAAAAA"
        );
        lockFile.Spaces["lore"] = space;

        return lockFile;
    }
    private static string Report(string source) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            embeddings: CreateProbeLock(),
            source: source
        );

        return (compilation.Diagnostics.HasErrors
            ? compilation.Diagnostics.FormatReport(source).ReplaceLineEndings(replacementText: " ")
            : ""
        );
    }
    private static string Source(string declaration) => $$"""
        schema: "puck.world.definition.v1"

        state {
        {{Space}}

            world {
                {{declaration}}
            }
        }

        """;
    private static string Value(string kind) => (kind switch {
        "Bool" => "true",
        "Fixed" => "1.5",
        "Text" => "\"x\"",
        _ => "1",
    });

    // Every described member whose admission turns on the row's kind, with the construct that carries it — except
    // `space`, whose own presence is what infers Vector, so a probe carrying it can never reach a different kind.
    public static TheoryData<string, string, WorldMemberPosition> Facets() {
        var data = new TheoryData<string, string, WorldMemberPosition>();

        foreach (var construct in WorldConstructs.Table.Constructs) {
            foreach (var member in construct.Members.Where(predicate: static member => (
                (member.AdmittedKinds.Count > 0) &&
                (member.Name != "space")
            ))) {
                data.Add(
                    p1: construct.Keyword,
                    p2: member.Name,
                    p3: member.Position
                );
            }
        }

        return data;
    }
    [MemberData(nameof(Facets))]
    [Theory]
    public void AKindConditionalMemberIsAdmittedOnExactlyTheKindsItNames(string keyword, string name, WorldMemberPosition position) {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(
            construct: out var construct,
            enclosing: "world",
            keyword: keyword
        ));

        var member = construct!.Members.Single(predicate: candidate => (
            (candidate.Position == position) &&
            string.Equals(
            a: candidate.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        )
        ));

        var refused = FullKindChoicesFor(keyword: keyword).Except(second: member.AdmittedKinds, comparer: StringComparer.Ordinal).ToArray();

        Assert.True(
            condition: (refused.Length > 0),
            userMessage: $"'{keyword}' admits only {FullKindsFor(keyword: keyword)}, so '{name}' naming {string.Join(
                separator: ", ",
                values: member.AdmittedKinds
            )} restricts nothing"
        );

        // Every kind the facet names, not just the first: a site enforcing a narrower set of its own passes on one
        // of them and refuses the rest.
        foreach (var kind in member.AdmittedKinds) {
            var admitted = Declaration(
                keyword: keyword,
                kind: kind,
                member: name,
                position: position
            );

            Assert.True(
                condition: (admitted.Length > 0),
                userMessage: $"no probe authors '{keyword}''s {position.ToString().ToLowerInvariant()} '{name}'"
            );
            Assert.Equal(
                actual: $"{kind}: {Report(source: Source(declaration: admitted))}",
                expected: $"{kind}: "
            );
        }

        var report = Report(source: Source(declaration: Declaration(
            keyword: keyword,
            kind: refused[0],
            member: name,
            position: position
        )));

        Assert.Contains(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"'{name}'"
        );

        // The refusal names the description's own kinds, so the two cannot drift apart.
        Assert.Contains(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: string.Join(
                separator: " or ",
                values: member.AdmittedKinds
            )
        );
    }
}
