using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: re-declaring the row set moves the store to the new ordinals in place rather than
/// replacing it, so every value a surviving row held rides across and anything holding the store keeps holding it.
/// </summary>
public sealed class ArenaRelayoutLawTests {
    private static WorldDefinition Document() => (Fixtures.BuildDocument() with {
        StateRaw = new(World: [new WorldStateRow(
                Name(value: "score"),
                CellKind.Int,
                Cells: [new StateCell(
                        StateRow.SlotKey,
                        CellValue.Int(value: 5L)
                    )]
            )]),
        Rules = [],
    });
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    [Fact]
    public void ANewlyDeclaredRowRelayoutsTheStoreInPlaceAndCarriesEveryOtherRowsValue() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var before = fixture.Server.Arena;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: new WorldStateRow(
                Name(value: "extra"),
                CellKind.Int,
                Cells: [new StateCell(
                        StateRow.SlotKey,
                        CellValue.Int(value: 7L)
                    )]
            )
        ));
        fixture.Step();

        var arena = fixture.Server.Arena;

        Assert.Same(
            actual: arena,
            expected: before
        );

        var catalog = arena.Catalog;

        Assert.True(condition: catalog.TryResolve(
            handle: out var score,
            lane: StateLane.Document,
            name: "score"
        ));
        Assert.True(condition: catalog.TryResolve(
            handle: out var extra,
            lane: StateLane.Document,
            name: "extra"
        ));

        var slot = catalog.Keys.Intern(name: StateRow.SlotKey);

        Assert.Equal(
            actual: arena.Read(
                key: slot,
                rowOrdinal: score.Ordinal
            )?.AsInt,
            expected: 5L
        );
        Assert.Equal(
            actual: arena.Read(
                key: slot,
                rowOrdinal: extra.Ordinal
            )?.AsInt,
            expected: 7L
        );
    }
}
