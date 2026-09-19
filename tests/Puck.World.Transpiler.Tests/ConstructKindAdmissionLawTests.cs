using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A member whose admission turns on the row's kind is admitted on exactly the kinds the description names,
/// and the refusal the lowering draws names those kinds rather than a list of its own.</summary>
/// <remarks>The subjects are enumerated from the table, so a kind-conditional member the description gains with no
/// probe here fails by name. Each subject is driven twice: once on a kind the facet admits, once on a kind the
/// construct's own <c>kind</c> choices carry that the facet does not.</remarks>
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

    private static string Declaration(string keyword, string member, WorldMemberPosition position, string kind) => ((keyword, member, position) switch {
        ("grid", "bounds", _) => $"grid probe : {kind} dimensions(width: 2, depth: 2) bounds(minimum: 0, maximum: 9, overflow: Saturate)",
        ("table", "advance", WorldMemberPosition.Cell) => $"table probe : {kind} {{\n            a = {Value(kind: kind)} advance(perSecond: 1)\n        }}",
        (_, "advance", _) => $"{keyword} probe : {kind} advance(perSecond: 1)",
        (_, "bounds", _) => $"{keyword} probe : {kind} bounds(minimum: 0, maximum: 9, overflow: Saturate)",
        (_, "embeds", _) => $"{keyword} probe : {kind} embeds(probeVectors, space: lore)",
        (_, "space", _) => $"{keyword} probe : {kind} space(lore)",
        _ => "",
    });
    private static string Report(string source) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
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

    // Every described member whose admission turns on the row's kind, with the construct that carries it.
    public static TheoryData<string, string, WorldMemberPosition> Facets() {
        var data = new TheoryData<string, string, WorldMemberPosition>();

        foreach (var construct in WorldConstructs.Table.Constructs) {
            foreach (var member in construct.Members.Where(predicate: static member => (member.AdmittedKinds.Count > 0))) {
                data.Add(
                    construct.Keyword,
                    member.Name,
                    member.Position
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

        Assert.True(condition: construct.TryGetMember(
            member: out var rowKind,
            name: "kind"
        ));

        var refused = rowKind!.Choices.Except(second: member.AdmittedKinds, comparer: StringComparer.Ordinal).ToArray();

        Assert.True(
            condition: (refused.Length > 0),
            userMessage: $"'{keyword}' admits only {string.Join(
                separator: ", ",
                values: rowKind.Choices
            )}, so '{name}' naming {string.Join(
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
