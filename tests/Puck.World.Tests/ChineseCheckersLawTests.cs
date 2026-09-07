using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>The shipped Chinese Checkers probe (<c>games/chinese-checkers.world.json</c>): the chain-jump shape
/// and the n-seat scores row wired into a real board, and its win condition — a seat's own row (<see
/// cref="Cell"/> reads it back) reaching every one of its pieces home.</summary>
public sealed class ChineseCheckersLawTests {
    private static WorldStateRow Row(WorldFixture fixture, string name) => WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: name)!;
    private static long Cell(WorldFixture fixture, string row, string key) => Row(fixture: fixture, name: row).Cells!.Single(predicate: c => (c.Key.Value == key)).Value;

    private static WorldDefinition Definition() => AuthoredGameFixtures.Program(module: "chinese-checkers");

    [Fact]
    public void TheShippedDocumentValidates() {
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(Definition(), out var reason), userMessage: reason);
    }

    [Fact]
    public void TheWinConditionFiresWhenTheLastPieceLands() {
        using var fixture = Fixtures.FreshServer(definition: Definition());

        // Seat 0 opens with both its pieces off the target cluster (30, 14). Landing the first (s0p1) there still
        // leaves the seat short a piece; landing the second (s0p0) is the LAST piece to land — the fact this law
        // proves, not merely that a piece can reach home at all.
        Assert.Equal(expected: 22L, actual: Cell(fixture: fixture, row: "pieceCellPrev", key: "s0p1"));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "pieceCell", Key: "s0p1", Value: 14L, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        Assert.Equal(expected: 1L, actual: Cell(fixture: fixture, row: "home0", key: "$value"));
        Assert.Equal(expected: -1L, actual: Cell(fixture: fixture, row: "winner", key: "$value"));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "pieceCell", Key: "s0p0", Value: 30L, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        Assert.Equal(expected: 2L, actual: Cell(fixture: fixture, row: "home0", key: "$value"));
        Assert.Equal(expected: 0L, actual: Cell(fixture: fixture, row: "winner", key: "$value"));
    }
}
