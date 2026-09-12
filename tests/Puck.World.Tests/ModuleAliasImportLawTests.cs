using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The law: one game module composes twice under two aliases and both play. <c>Fixtures/twin-tictactoe-host.world.json</c>
/// imports <c>games/tictactoe.world.json</c> as <c>a</c> and as <c>b</c>; every row, lattice, and rule the module
/// declares lands twice under <c>a_</c>/<c>b_</c>, the module's own references follow, and a move on one table
/// leaves the other untouched.
/// </summary>
public sealed class ModuleAliasImportLawTests {
    private static string FixturePath() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return Path.Combine(directory!.FullName, "tests", "Puck.World.Tests", "Fixtures", "twin-tictactoe-host.world.json");
    }
    private static WorldDefinition LoadTwinHost() {
        Assert.True(condition: WorldDefinitionLoader.TryLoadFile(path: FixturePath(), definition: out var definition, reason: out var reason), userMessage: reason);

        return definition!;
    }
    private static WorldStateRow Row(WorldFixture fixture, string name) {
        var row = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: name);

        Assert.NotNull(@object: row);

        return row!;
    }
    private static long Cell(WorldStateRow row, string key) => (row.Cells?.SingleOrDefault(predicate: c => (c.Key.Value == key))?.Value
        ?? ((row.EffectiveDomain is StateDomain.CellsOf board) ? board.Empty : throw new InvalidOperationException($"missing {row.Name}[{key}]")));
    private static long Slot(WorldFixture fixture, string name) => Cell(row: Row(fixture: fixture, name: name), key: WorldStateRow.SlotKey);
    private static void Write(WorldFixture fixture, string row, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: WorldPrincipal.Console, Row: row, Key: WorldStateRow.SlotKey, Value: value, Kind: WorldDocumentWriteKind.Set
    ));
    private static void Place(WorldFixture fixture, string alias, long cell, long request) {
        Write(fixture: fixture, row: $"{alias}_tttMoveCell", value: cell);
        Write(fixture: fixture, row: $"{alias}_tttMoveRequest", value: request);

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }
    }

    [Fact]
    public void EachAliasDeclaresItsOwnRowsLatticesAndRules() {
        var definition = LoadTwinHost();
        var rows = definition.State.Select(selector: static row => row.Name.Value).ToHashSet(comparer: StringComparer.Ordinal);
        var rules = (definition.Rules ?? []).Select(selector: static rule => rule.Name.Value).ToHashSet(comparer: StringComparer.Ordinal);
        var lattices = (definition.StateRaw?.Lattices ?? []).Select(selector: static lattice => lattice.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Null(@object: definition.Imports);
        Assert.Contains(expected: "a_tttBoard", collection: rows);
        Assert.Contains(expected: "b_tttBoard", collection: rows);
        Assert.DoesNotContain(expected: "tttBoard", collection: rows);
        Assert.Contains(expected: "a_ttt-place-mark", collection: rules);
        Assert.Contains(expected: "b_ttt-place-mark", collection: rules);
        Assert.Contains(expected: "a_tttCube", collection: lattices);
        Assert.Contains(expected: "b_tttCube", collection: lattices);

        // The module's own references follow its declarations: the board's domain names the aliased cube, and the
        // win check's expressions read the aliased masks over the aliased cube.
        var board = definition.State.Single(predicate: static row => (row.Name.Value == "b_tttBoard"));

        Assert.Equal(expected: "b_tttCube", actual: ((StateDomain.CellsOf)board.Domain!).Topology);

        var winCheck = (definition.Rules ?? []).Single(predicate: static rule => (rule.Name.Value == "b_ttt-check-win"));
        var maskEffect = winCheck.Effects.OfType<ActionEffect.SetState>().First(predicate: static effect => (effect.State == "b_tttMaskX"));

        Assert.Equal(expected: "$board:mask:b_tttBoard:1:1", actual: maskEffect.Expression!.Text);
        Assert.Contains(expectedSubstring: "boardShift(b_tttMaskX, b_tttCube, L0)", actualString: winCheck.Effects.OfType<ActionEffect.SetState>().First(predicate: static effect => (effect.State == "b_tttXWin")).Expression!.Text!);
    }
    [Fact]
    public void BothTablesPlayIndependently() {
        using var fixture = Fixtures.FreshServer(definition: LoadTwinHost());

        Place(fixture: fixture, alias: "a", cell: 0, request: 1);
        Place(fixture: fixture, alias: "b", cell: 5, request: 1);

        Assert.Equal(expected: 1L, actual: Cell(row: Row(fixture: fixture, name: "a_tttBoard"), key: "0"));
        Assert.Equal(expected: 0L, actual: Cell(row: Row(fixture: fixture, name: "a_tttBoard"), key: "5"));
        Assert.Equal(expected: 1L, actual: Cell(row: Row(fixture: fixture, name: "b_tttBoard"), key: "5"));
        Assert.Equal(expected: 0L, actual: Cell(row: Row(fixture: fixture, name: "b_tttBoard"), key: "0"));
        Assert.Equal(expected: 1L, actual: Slot(fixture: fixture, name: "a_tttMoveApplied"));
        Assert.Equal(expected: 1L, actual: Slot(fixture: fixture, name: "b_tttMoveApplied"));
        Assert.Equal(expected: 2L, actual: Slot(fixture: fixture, name: "a_tttActive"));
        Assert.Equal(expected: 2L, actual: Slot(fixture: fixture, name: "b_tttActive"));

        // A second move on one table advances only that table.
        Place(fixture: fixture, alias: "a", cell: 1, request: 2);

        Assert.Equal(expected: 2L, actual: Cell(row: Row(fixture: fixture, name: "a_tttBoard"), key: "1"));
        Assert.Equal(expected: 2L, actual: Slot(fixture: fixture, name: "a_tttMoveCount"));
        Assert.Equal(expected: 1L, actual: Slot(fixture: fixture, name: "a_tttActive"));
        Assert.Equal(expected: 1L, actual: Slot(fixture: fixture, name: "b_tttMoveCount"));
        Assert.Equal(expected: 2L, actual: Slot(fixture: fixture, name: "b_tttActive"));
        Assert.Equal(expected: 0L, actual: Cell(row: Row(fixture: fixture, name: "b_tttBoard"), key: "1"));

        // An occupied cell is refused on the table it is occupied on, and only there.
        Place(fixture: fixture, alias: "b", cell: 5, request: 2);

        Assert.Equal(expected: 2L, actual: Slot(fixture: fixture, name: "b_tttMoveApplied"));
        Assert.Equal(expected: 1L, actual: Slot(fixture: fixture, name: "b_tttMoveCount"));
        Assert.Equal(expected: 2L, actual: Slot(fixture: fixture, name: "b_tttActive"));
    }
    [Fact]
    public void CompositionReadBackNamesEachAlias() {
        Assert.True(condition: WorldDefinitionFileSource.TryDescribeComposition(path: FixturePath(), layers: out var layers, reason: out var reason), userMessage: reason);

        var aliased = layers.Where(predicate: static layer => (layer.Alias is not null)).Select(selector: static layer => layer.Alias!).ToArray();

        Assert.Equal(expected: ["a", "b"], actual: aliased);
        Assert.All(collection: layers.Where(predicate: static layer => layer.Path.EndsWith(value: "tictactoe.world.json", comparisonType: StringComparison.Ordinal)), action: static layer => Assert.Equal(expected: ["rules", "state"], actual: layer.Keys.OrderBy(keySelector: static key => key, comparer: StringComparer.Ordinal).ToArray()));
    }
    [Fact]
    public void OneAliasTwiceFoldsAndAMalformedAliasRefusesByName() {
        using var files = new TempWorldDirectory();
        var module = File.ReadAllText(path: Path.Combine(Path.GetDirectoryName(path: FixturePath())!, "..", "..", "..", "src", "Puck.World", "Assets", "worlds", "games", "tictactoe.world.json"));

        files.WriteText(name: "basis.world.json", text: System.Text.Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes()));
        files.WriteText(name: "tictactoe.world.json", text: module);

        // Under one alias twice the two copies agree at every path and fold to one module.
        var twice = files.WriteText(name: "twice.world.json", text: /*lang=json*/ """
            { "basis": "basis.world.json", "imports": [{ "document": "tictactoe.world.json", "as": "a" }, { "document": "tictactoe.world.json", "as": "a" }] }
            """);

        Assert.True(condition: WorldDefinitionLoader.TryLoadFile(path: twice, definition: out var folded, reason: out var foldReason), userMessage: foldReason);
        Assert.Single(collection: folded!.State, predicate: static row => (row.Name.Value == "a_tttBoard"));

        var hyphen = files.WriteText(name: "hyphen.world.json", text: /*lang=json*/ """
            { "basis": "basis.world.json", "imports": [{ "document": "tictactoe.world.json", "as": "b-2" }] }
            """);

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(path: hyphen, definition: out _, reason: out var hyphenReason));
        Assert.Contains(expectedSubstring: "letters, digits, and underscores", actualString: hyphenReason);

        var bare = files.WriteText(name: "bare.world.json", text: /*lang=json*/ """
            { "basis": "basis.world.json", "imports": ["tictactoe.world.json"] }
            """);

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(path: bare, definition: out _, reason: out var bareReason));
        Assert.Contains(expectedSubstring: "\"document\"", actualString: bareReason);
    }
}
