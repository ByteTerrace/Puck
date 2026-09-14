using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The law: a module's names are private by default, and a host binds only what the module exports. A host that
/// imports <c>games/tictactoe.world.json</c> as <c>a</c> drives the module through its exported actions
/// (<c>a_tttMoveCell</c>, <c>a_tttMoveRequest</c>) from a rule and from an interaction between its kits, reads its
/// exported rows, and binds a HUD element to an exported binding; the same host binding an interaction effect, a
/// gate, a HUD element, or a placement's board facet to a name the module declares and does not export under that
/// facet refuses at load, naming the
/// name, the module, and the export list that would admit it. The composed document carries no <c>exports</c>, and
/// the composition read-back describes each module's surface.
/// </summary>
public sealed class ModuleExportsLawTests {
    private const string TicTacToeHost = /*lang=json*/ """
        {
          "basis": "basis.world.json",
          "imports": [{ "document": "tictactoe.world.json", "as": "a" }],
          "rules": [
            {
              "name": "drive-a",
              "effects": [
                { "$type": "setState", "state": "a_tttMoveCell", "value": 4 },
                { "$type": "setState", "state": "a_tttMoveRequest", "value": 1 }
              ]
            },
            {
              "name": "watch-a",
              "gate": { "$type": "compareState", "state": "READ_ROW", "comparison": "Equal", "value": 2 },
              "effects": [ { "$type": "setState", "state": "hostSawTurn", "value": 1 } ]
            }
          ],
          "state": { "world": [ { "name": "hostSawTurn", "kind": "Int", "value": 0 }, { "name": "player", "kind": "Int", "capacity": 4 } ] },
          "properties": { "names": [ "player" ] },
          "interactions": { "interactions": [ { "name": "touch", "left": "player", "right": "player", "coOccurrence": "Distance", "range": 1,
            "effects": [ { "$type": "setState", "state": "ACTION_ROW", "value": 0 } ] } ] },
          "hud": { "panels": [ {
            "id": "ttt", "layer": "Under", "style": "Strip", "rect": { "x": 0, "y": 0, "width": 0.5, "height": 0.1 },
            "elements": [ { "id": "winner", "kind": "Text", "style": "Primary", "rect": { "x": 0, "y": 0, "width": 1, "height": 1 }, "binding": "BINDING_TOKEN" } ]
          } ] }
        }
        """;

    private static long Cell(WorldFixture fixture, string row, string key) {
        var found = WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: row
        );

        Assert.NotNull(@object: found);

        return (found!.Cells?.SingleOrDefault(predicate: cell => (cell.Key.Value == key))?.Value ?? 0L);
    }
    private static string ModuleText(string module) =>
        File.ReadAllText(path: Path.Combine(
            RepositoryRoot(),
            "src",
            "Puck.World",
            "Assets",
            "worlds",
            "games",
            $"{module}.world.json"
        ));
    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (
            (directory is not null) &&
            !File.Exists(path: Path.Combine(
            path1: directory.FullName,
            path2: "Puck.slnx"
        ))
        ) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static string WriteHost(TempWorldDirectory files, string actionRow = "a_tttMoveCell", string readRow = "a_tttActive", string bindingToken = "state.a_tttWinner", string? moduleText = null) {
        files.WriteText(
            name: "basis.world.json",
            text: System.Text.Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes())
        );
        files.WriteText(
            name: "tictactoe.world.json",
            text: (moduleText ?? ModuleText(module: "tictactoe"))
        );

        return files.WriteText(
            name: "host.world.json",
            text: TicTacToeHost
            .Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: actionRow,
                oldValue: "ACTION_ROW"
            )
            .Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: readRow,
                oldValue: "READ_ROW"
            )
            .Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: bindingToken,
                oldValue: "BINDING_TOKEN"
            )
        );
    }

    [InlineData("a_tttBoard", "a_tttActive", "state.a_tttWinner", "a_tttBoard", "exports.actions", "$.interactions.interactions[0].effects[0].state")]
    [InlineData("a_tttMoveCell", "a_tttMaskX", "state.a_tttWinner", "a_tttMaskX", "exports.reads", "$.rules[1].gate.state")]
    [InlineData("a_tttMoveCell", "a_tttActive", "state.a_tttMoveCell", "a_tttMoveCell", "exports.bindings", "$.hud.panels[0].elements[0].binding")]
    [Theory]
    public void ABindingToANameTheModuleDoesNotExportRefusesByName(string actionRow, string readRow, string bindingToken, string name, string list, string path) {
        using var files = new TempWorldDirectory();
        var hostPath = WriteHost(
            files: files,
            actionRow: actionRow,
            readRow: readRow,
            bindingToken: bindingToken
        );

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(
            path: hostPath,
            definition: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"'{name}'"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: list
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: path
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "tictactoe.world.json as a"
        );
    }
    [Fact]
    public void AHostDrivesReadsAndBindsTheModuleThroughItsExports() {
        using var files = new TempWorldDirectory();
        var hostPath = WriteHost(files: files);

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path: hostPath,
                definition: out var definition,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Null(@object: definition!.Exports);
        Assert.Contains(
            collection: definition.State,
            filter: static row => (row.Name.Value == "a_tttMoveCell")
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }

        // The host's rule drove the module through its exported actions and the module played the move; the host's
        // gate read the exported turn row after the module advanced it.
        Assert.Equal(
            expected: 1L,
            actual: Cell(
                fixture: fixture,
                key: "4",
                row: "a_tttBoard"
            )
        );
        Assert.Equal(
            expected: 2L,
            actual: Cell(
                fixture: fixture,
                key: WorldStateRow.SlotKey,
                row: "a_tttActive"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Cell(
                fixture: fixture,
                key: WorldStateRow.SlotKey,
                row: "hostSawTurn"
            )
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryDescribeComposition(
                layers: out var layers,
                path: hostPath,
                reason: out var describeReason
            ),
            userMessage: describeReason
        );

        var module = layers.Single(predicate: static layer => (layer.Alias == "a"));

        Assert.NotNull(@object: module.Exports);
        Assert.Equal(
            expected: "reads:tttActive,tttBoard,tttCube,tttMoveApplied,tttMoveCount,tttWinner actions:tttMoveCell,tttMoveRequest bindings:tttActive,tttMoveCount,tttWinner",
            actual: module.Exports!.Describe()
        );
        Assert.All(
            collection: layers.Where(predicate: static layer => (layer.Alias is null)),
            action: static layer => Assert.Null(@object: layer.Exports)
        );
    }
    [Fact]
    public void APlacementBoardFacetBindsOnlyAnExportedOccupancyRow() {
        var fixtures = Path.Combine(
            path1: RepositoryRoot(),
            path2: "tests",
            path3: "Puck.World.Tests",
            path4: "Fixtures"
        );
        var host = ((JsonObject)JsonNode.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: fixtures,
            path2: "minimal-hexlines-host.world.json"
        )))!);

        host[propertyName: "basis"] = Path.GetFullPath(path: Path.Combine(
            path1: fixtures,
            path2: host[propertyName: "basis"]!.GetValue<string>()
        ));
        host[propertyName: "imports"]![index: 0]![propertyName: "document"] = Path.GetFullPath(path: Path.Combine(
            path1: fixtures,
            path2: host[propertyName: "imports"]![index: 0]![propertyName: "document"]!.GetValue<string>()
        ));

        using var files = new TempWorldDirectory();
        var exported = files.WriteText(
            name: "exported.world.json",
            text: host.ToJsonString()
        );

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path: exported,
                definition: out _,
                reason: out var exportedReason
            ),
            userMessage: exportedReason
        );

        host[propertyName: "placements"]![propertyName: "rows"]![index: 0]![propertyName: "board"]![propertyName: "occupancy"] = "hexStoneCell";

        var unexported = files.WriteText(
            name: "unexported.world.json",
            text: host.ToJsonString()
        );

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(
            path: unexported,
            definition: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "'hexStoneCell'"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "exports.actions"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "$.placements.rows[0].board.occupancy"
        );
    }
    [Fact]
    public void ASiblingImportBindsOnlyWhatItsSiblingExports() {
        using var files = new TempWorldDirectory();

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
            { "basis": "basis.world.json", "imports": [{ "document": "private.world.json" }, { "document": "reader.world.json" }] }
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
            { "basis": "basis.world.json", "imports": [{ "document": "public.world.json" }, { "document": "reader.world.json" }] }
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
    public void AnExportNamingNothingTheModuleDeclaresRefusesByName() {
        var module = ((JsonObject)JsonNode.Parse(json: ModuleText(module: "tictactoe"))!);

        module[propertyName: "exports"]![propertyName: "reads"]!.AsArray().Add(value: "tttNoSuchRow");

        using var files = new TempWorldDirectory();
        var hostPath = WriteHost(
            files: files,
            moduleText: module.ToJsonString()
        );

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(
            path: hostPath,
            definition: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "exports.reads names 'tttNoSuchRow', which the module does not declare"
        );
    }
    [Fact]
    public void ExportsOnADocumentLoadedAsAWorldRefuse() {
        var world = ((JsonObject)JsonNode.Parse(json: System.Text.Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes()))!);

        world[propertyName: "exports"] = new JsonObject { [propertyName: "reads"] = new JsonArray() };

        using var files = new TempWorldDirectory();
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
