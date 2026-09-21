using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: re-declaring the row set installs a prepared replacement arena at the new ordinals,
/// preserving every surviving row's value.
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
    public void ANewlyDeclaredRowInstallsThePreparedStoreAndCarriesEveryOtherRowsValue() {
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

        Assert.NotSame(
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
