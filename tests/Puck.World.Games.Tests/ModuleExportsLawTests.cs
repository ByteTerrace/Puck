using Puck.Testing;
using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>
/// The law: a module's names are private by default, and a host binds only what the module exports. A host that
/// imports <c>games/tictactoe.puck</c> as <c>a</c> drives the module through its exported actions
/// (<c>a$tttMoveCell</c>, <c>a$tttMoveRequest</c>) from a rule and from an interaction between its kits, reads its
/// exported rows, and binds a HUD element to an exported binding; the same host binding an interaction effect, a
/// gate, a HUD element, or a placement's board facet to a name the module declares and does not export under that
/// facet refuses at load, naming the
/// name, the module, and the export list that would admit it. The composed document carries no <c>exports</c>, and
/// the composition read-back describes each module's surface.
/// </summary>
public sealed class ModuleExportsLawTests {
    private static long Cell(WorldFixture fixture, string row, string key) {
        var found = WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.ExportDefinition().State,
            name: row
        );

        Assert.NotNull(@object: found);

        return (found!.Cells?.SingleOrDefault(predicate: cell => (cell.Key.Value == key))?.Value.Raw ?? 0L);
    }

    [Fact]
    public void ASiblingImportBindsOnlyWhatItsSiblingExports() {
        using var files = new TemporaryDirectory();

        files.WriteText(
            name: "basis.world.json",
            text: System.Text.Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes())
        );
        files.WriteText(
            name: "reader.world.json",
            text: /*lang=json*/ """
            { "rules": [ { "name": "mirror", "gate": { "$type": "compareState", "state": "flag", "comparison": "Equal", "value": 1 },
                           "effects": [ { "$type": "setState", "state": "mirrored", "value": 1 } ] } ],
              "state": { "world": [ { "name": "mirrored", "kind": "Int", "value": 0 } ] } }
            """
        );
        files.WriteText(
            name: "private.world.json",
            text: /*lang=json*/ """
            { "state": { "world": [ { "name": "flag", "kind": "Int", "value": 1 } ] } }
            """
        );
        files.WriteText(
            name: "public.world.json",
            text: /*lang=json*/ """
            { "exports": { "reads": [ "flag" ] }, "state": { "world": [ { "name": "flag", "kind": "Int", "value": 1 } ] } }
            """
        );

        var refused = files.WriteText(
            name: "refused.world.json",
            text: /*lang=json*/ """
            { "basis": "basis", "imports": [{ "document": "private" }, { "document": "reader" }] }
            """
        );

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(
            path: refused,
            definition: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "reader.world.json names 'flag'"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "private.world.json declares and does not export under exports.reads"
        );

        var admitted = files.WriteText(
            name: "admitted.world.json",
            text: /*lang=json*/ """
            { "basis": "basis", "imports": [{ "document": "public" }, { "document": "reader" }] }
            """
        );

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path: admitted,
                definition: out var definition,
                reason: out var admittedReason
            ),
            userMessage: admittedReason
        );

        using var fixture = Fixtures.FreshServer(definition: definition!);

        fixture.Step();
        fixture.Step();

        Assert.Equal(
            expected: 1L,
            actual: Cell(
                fixture: fixture,
                key: WorldStateRow.SlotKey,
                row: "mirrored"
            )
        );
    }
    [Fact]
    public void ExportsOnADocumentLoadedAsAWorldRefuse() {
        var world = ((JsonObject)JsonNode.Parse(json: System.Text.Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes()))!);

        world[propertyName: "exports"] = new JsonObject { [propertyName: "reads"] = new JsonArray() };

        using var files = new TemporaryDirectory();
        var path = files.WriteText(
            name: "module-as-world.world.json",
            text: world.ToJsonString()
        );

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(
            path: path,
            definition: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "exports [none] survived to validation"
        );
    }
}
