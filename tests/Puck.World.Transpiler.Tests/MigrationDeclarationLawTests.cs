using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Rewriting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The declaration law: what a migration says it reshapes is what it is held to. A declared path covers
/// everything beneath it and nothing beside it, a <c>*</c> segment stands for exactly one key or index, and an
/// empty declaration admits no change to the document at all.</summary>
public class MigrationDeclarationLawTests {
    private sealed class Declared(IReadOnlyList<string> members) : PuckMigration {
        public override string Name => "declared";
        public override IReadOnlyList<string> ReshapedMembers { get; } = members;
        public override string Summary => "declares whatever the law hands it";

        public override DocumentNode Apply(DocumentNode document) => document;
    }

    private static PuckMigration Migration(params string[] members) => new Declared(members: members);
    private static JsonNode Parse(string json) => JsonNode.Parse(json: json)!;

    [Fact]
    public void IdenticalDocumentsDifferNowhere() => Assert.Null(@object: Migration().UndeclaredDifference(
        after: Parse(json: """{ "state": { "rows": [ { "name": "hp", "value": 1 } ] } }"""),
        before: Parse(json: """{ "state": { "rows": [ { "name": "hp", "value": 1 } ] } }""")
    ));
    [Fact]
    public void AnEmptyDeclarationAdmitsNoChange() {
        var migration = Migration();

        Assert.NotNull(@object: migration.UndeclaredDifference(
            after: Parse(json: """{ "documentId": "b" }"""),
            before: Parse(json: """{ "documentId": "a" }""")
        ));
        Assert.NotNull(@object: migration.UndeclaredDifference(
            after: Parse(json: """{ "documentId": "a", "extra": 1 }"""),
            before: Parse(json: """{ "documentId": "a" }""")
        ));
        Assert.NotNull(@object: migration.UndeclaredDifference(
            after: Parse(json: "{ }"),
            before: Parse(json: """{ "documentId": "a" }""")
        ));
    }
    [Fact]
    public void ADeclaredPathCoversEverythingBeneathIt() => Assert.Null(@object: Migration("state/rows").UndeclaredDifference(
        after: Parse(json: """{ "documentId": "a", "state": { "rows": [ { "name": "hp", "value": 2 } ] } }"""),
        before: Parse(json: """{ "documentId": "a", "state": { "rows": [ { "name": "hp", "value": 1 } ] } }""")
    ));
    [Fact]
    public void ADeclaredPathCoversNothingBesideIt() {
        var difference = Migration("state/rows").UndeclaredDifference(
            after: Parse(json: """{ "documentId": "b", "state": { "rows": [] } }"""),
            before: Parse(json: """{ "documentId": "a", "state": { "rows": [] } }""")
        );

        Assert.NotNull(@object: difference);
        Assert.StartsWith(
            actualString: difference,
            expectedStartString: "documentId:"
        );
    }
    [Fact]
    public void AStarStandsForOneSegment() {
        Assert.Null(@object: Migration("state/rows/*/value").UndeclaredDifference(
            after: Parse(json: """{ "state": { "rows": [ { "value": 1 }, { "value": 2 } ] } }"""),
            before: Parse(json: """{ "state": { "rows": [ { "value": 0 }, { "value": 0 } ] } }""")
        ));
        // One key, never a run of them: the array index between `rows` and `value` is a segment of its own.
        Assert.NotNull(@object: Migration("state/*/value").UndeclaredDifference(
            after: Parse(json: """{ "state": { "rows": [ { "value": 1 } ] } }"""),
            before: Parse(json: """{ "state": { "rows": [ { "value": 0 } ] } }""")
        ));
    }
    // A declaration deeper than the difference covers nothing: a member that changed kind wholesale is not
    // excused by naming something inside it.
    [Fact]
    public void ADeclarationDeeperThanTheDifferenceCoversNothing() => Assert.NotNull(@object: Migration("state/rows/0/value").UndeclaredDifference(
        after: Parse(json: """{ "state": 1 }"""),
        before: Parse(json: """{ "state": { "rows": [ { "value": 0 } ] } }""")
    ));
}
