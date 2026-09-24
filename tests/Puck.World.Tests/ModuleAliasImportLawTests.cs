using Puck.Commands;
using Puck.Testing;
using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The law: one game module composes twice under two aliases and both play. <c>Fixtures/twin-tictactoe-host.world.json</c>
/// imports <c>games/tictactoe.puck</c> as <c>a</c> and as <c>b</c>; every row, lattice, and rule the module
/// declares lands twice under <c>a$</c>/<c>b$</c>, the module's own references follow, and a move on one table
/// leaves the other untouched.
/// </summary>
public sealed class ModuleAliasImportLawTests {
    private static long Cell(WorldStateRow row, string key) => (row.Cells?.SingleOrDefault(predicate: c => (c.Key.Value == key))?.Value.AsInt
        ?? ((row.EffectiveDomain is StateDomain.CellsOf board)
        ? board.Empty
        : throw new InvalidOperationException(message: $"missing {row.Name}[{key}]")));
    private static string FixturePath() => RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Tests/Fixtures/twin-tictactoe-host.world.json");
    private static WorldDefinition LoadTwinHost() {
        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path: FixturePath(),
                definition: out var definition,
                reason: out var reason
            ),
            userMessage: reason
        );

        return definition!;
    }
    private static void Place(WorldFixture fixture, string alias, long cell, long request) {
        Write(
            fixture: fixture,
            row: $"{alias}$tttMoveCell",
            value: cell
        );
        Write(
            fixture: fixture,
            row: $"{alias}$tttMoveRequest",
            value: request
        );

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }
    }
    private static WorldStateRow Row(WorldFixture fixture, string name) {
        var row = WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: name
        );

        Assert.NotNull(@object: row);

        return row!;
    }
    private static long Slot(WorldFixture fixture, string name) => Cell(
        row: Row(
            fixture: fixture,
            name: name
        ),
        key: WorldStateRow.SlotKey
    );
    private static void Write(WorldFixture fixture, string row, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: Principal.Console,
        Row: row,
        Key: WorldStateRow.SlotKey,
        Value: value,
        Kind: WorldDocumentWriteKind.Set
    ));

    [Fact]
    public void BothTablesPlayIndependently() {
        using var fixture = Fixtures.FreshServer(definition: LoadTwinHost());

        Place(
            alias: "a",
            cell: 0,
            fixture: fixture,
            request: 1
        );
        Place(
            alias: "b",
            cell: 5,
            fixture: fixture,
            request: 1
        );

        Assert.Equal(
            expected: 1L,
            actual: Cell(
                row: Row(
                    fixture: fixture,
                    name: "a$tttBoard"
                ),
                key: "0"
            )
        );
        Assert.Equal(
            expected: 0L,
            actual: Cell(
                row: Row(
                    fixture: fixture,
                    name: "a$tttBoard"
                ),
                key: "5"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Cell(
                row: Row(
                    fixture: fixture,
                    name: "b$tttBoard"
                ),
                key: "5"
            )
        );
        Assert.Equal(
            expected: 0L,
            actual: Cell(
                row: Row(
                    fixture: fixture,
                    name: "b$tttBoard"
                ),
                key: "0"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Slot(
                fixture: fixture,
                name: "a$tttMoveApplied"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Slot(
                fixture: fixture,
                name: "b$tttMoveApplied"
            )
        );
        Assert.Equal(
            expected: 2L,
            actual: Slot(
                fixture: fixture,
                name: "a$tttActive"
            )
        );
        Assert.Equal(
            expected: 2L,
            actual: Slot(
                fixture: fixture,
                name: "b$tttActive"
            )
        );

        // A second move on one table advances only that table.
        Place(
            alias: "a",
            cell: 1,
            fixture: fixture,
            request: 2
        );

        Assert.Equal(
            expected: 2L,
            actual: Cell(
                row: Row(
                    fixture: fixture,
                    name: "a$tttBoard"
                ),
                key: "1"
            )
        );
        Assert.Equal(
            expected: 2L,
            actual: Slot(
                fixture: fixture,
                name: "a$tttMoveCount"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Slot(
                fixture: fixture,
                name: "a$tttActive"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Slot(
                fixture: fixture,
                name: "b$tttMoveCount"
            )
        );
        Assert.Equal(
            expected: 2L,
            actual: Slot(
                fixture: fixture,
                name: "b$tttActive"
            )
        );
        Assert.Equal(
            expected: 0L,
            actual: Cell(
                row: Row(
                    fixture: fixture,
                    name: "b$tttBoard"
                ),
                key: "1"
            )
        );

        // An occupied cell is refused on the table it is occupied on, and only there.
        Place(
            alias: "b",
            cell: 5,
            fixture: fixture,
            request: 2
        );

        Assert.Equal(
            expected: 2L,
            actual: Slot(
                fixture: fixture,
                name: "b$tttMoveApplied"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Slot(
                fixture: fixture,
                name: "b$tttMoveCount"
            )
        );
        Assert.Equal(
            expected: 2L,
            actual: Slot(
                fixture: fixture,
                name: "b$tttActive"
            )
        );
    }
    [Fact]
    public void CompositionReadBackNamesEachAlias() {
        Assert.True(
            condition: WorldDefinitionFileSource.TryDescribeComposition(
                path: FixturePath(),
                layers: out var layers,
                reason: out var reason
            ),
            userMessage: reason
        );

        var aliased = layers.Where(predicate: static layer => (layer.Alias is not null)).Select(selector: static layer => layer.Alias!).ToArray();

        Assert.Equal(
            actual: aliased,
            expected: ["a", "b"]
        );
        // Both aliased layers are the tictactoe module, carried by its source.
        Assert.All(
            collection: layers.Where(predicate: static layer => (layer.Alias is not null)),
            action: static layer => Assert.Equal(
                expected: ["rules", "state"],
                actual: layer.Keys.OrderBy(
                    keySelector: static key => key,
                    comparer: StringComparer.Ordinal
                ).ToArray()
            )
        );
    }
    [Fact]
    public void EachAliasDeclaresItsOwnRowsLatticesAndRules() {
        var definition = LoadTwinHost();
        var rows = definition.State.Select(selector: static row => row.Name.Value).ToHashSet(comparer: StringComparer.Ordinal);
        var rules = (definition.Rules ?? []).Select(selector: static rule => rule.Name.Value).ToHashSet(comparer: StringComparer.Ordinal);
        var lattices = (definition.StateRaw?.Lattices ?? []).Select(selector: static lattice => lattice.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Null(@object: definition.Imports);
        Assert.Contains(
            collection: rows,
            expected: "a$tttBoard"
        );
        Assert.Contains(
            collection: rows,
            expected: "b$tttBoard"
        );
        Assert.DoesNotContain(
            collection: rows,
            expected: "tttBoard"
        );
        Assert.Contains(
            collection: rules,
            expected: "a$ttt-place-mark"
        );
        Assert.Contains(
            collection: rules,
            expected: "b$ttt-place-mark"
        );
        Assert.Contains(
            collection: lattices,
            expected: "a$tttCube"
        );
        Assert.Contains(
            collection: lattices,
            expected: "b$tttCube"
        );

        // The module's own references follow its declarations: the board's domain names the aliased cube, and the
        // win check's expressions read the aliased masks over the aliased cube.
        var board = definition.State.Single(predicate: static row => (row.Name.Value == "b$tttBoard"));

        Assert.Equal(
            expected: "b$tttCube",
            actual: ((StateDomain.CellsOf)board.Domain!).Topology
        );

        var winCheck = (definition.Rules ?? []).Single(predicate: static rule => (rule.Name.Value == "b$ttt-check-win"));
        var maskEffect = winCheck.Effects.OfType<ActionEffect.SetState>().First(predicate: static effect => (effect.State == "b$tttMaskX"));

        Assert.Equal(
            expected: "`$board:mask:b$tttBoard:1:1`",
            actual: ExpressionSpelling.Print(program: maskEffect.Expression!)
        );
        Assert.Contains(
            expectedSubstring: "boardShift(`b$tttMaskX`, `b$tttCube`, L0)",
            actualString: ExpressionSpelling.Print(program: winCheck.Effects.OfType<ActionEffect.SetState>().First(predicate: static effect => (effect.State == "b$tttXWin")).Expression!)
        );
    }
    [Fact]
    public void OneAliasTwiceFoldsAndAMalformedAliasRefusesByName() {
        using var files = new TemporaryDirectory();
        var module = System.Text.Encoding.UTF8.GetString(bytes: Puck.Testing.ShippedWorldDocuments.Read(path: Path.Combine(
            Path.GetDirectoryName(path: FixturePath())!,
            "..",
            "..",
            "..",
            "src",
            "Puck.World",
            "Assets",
            "worlds",
            "games",
            "tictactoe.puck"
        )));

        files.WriteText(
            name: "basis.world.json",
            text: System.Text.Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes())
        );
        files.WriteText(
            name: "tictactoe.world.json",
            text: module
        );

        // Under one alias twice the two copies agree at every path and fold to one module.
        var twice = files.WriteText(
            name: "twice.world.json",
            text: /*lang=json*/ """
            { "basis": "basis", "imports": [{ "document": "tictactoe", "as": "a" }, { "document": "tictactoe", "as": "a" }] }
            """
        );

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path: twice,
                definition: out var folded,
                reason: out var foldReason
            ),
            userMessage: foldReason
        );
        Assert.Single(
            collection: folded!.State,
            predicate: static row => (row.Name.Value == "a$tttBoard")
        );

        var hyphen = files.WriteText(
            name: "hyphen.world.json",
            text: /*lang=json*/ """
            { "basis": "basis", "imports": [{ "document": "tictactoe", "as": "b-2" }] }
            """
        );

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(
            path: hyphen,
            definition: out _,
            reason: out var hyphenReason
        ));
        Assert.Contains(
            actualString: hyphenReason,
            expectedSubstring: "letters, digits, and underscores"
        );

        var bare = files.WriteText(
            name: "bare.world.json",
            text: /*lang=json*/ """
            { "basis": "basis", "imports": ["tictactoe"] }
            """
        );

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(
            path: bare,
            definition: out _,
            reason: out var bareReason
        ));
        Assert.Contains(
            actualString: bareReason,
            expectedSubstring: "\"document\""
        );
    }
}
