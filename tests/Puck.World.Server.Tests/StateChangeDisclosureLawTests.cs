using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

// A replica-tier recipient's row replacements come from WorldStateChangeDisclosure. Seeing one requires a replica
// session, which a headless console run cannot open, so the law drives the filter directly.
public sealed class StateChangeDisclosureLawTests {
    [Fact]
    public void ARestrictedRowIsReplacedOnlyWhenWhatTheRecipientReadsMoves() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: CellName.Parse(candidate: "hand"), Kind: CellKind.Int,
                Visibility: new StateVisibility(Hidden: HiddenCells.Omit),
                Cells: [
                    new StateCell(Key: CellName.Parse(candidate: "pub"), Value: CellValue.Int(value: 3)),
                    new StateCell(Key: CellName.Parse(candidate: "secret"), Value: CellValue.Int(value: 7),
                        Visibility: new StateVisibility(Readers: ["seat1"])),
                ])])
        };
        using var host = HostRow.Build(name: "boot", definition: definition);
        var server = host.Server;
        var arena = server.Arena;

        Assert.True(arena.Catalog.TryResolve(handle: out var hand, lane: StateLane.Document, name: "hand"));
        Assert.True(arena.Keys.TryResolve(key: out var pub, name: CellName.Parse(candidate: "pub")));
        Assert.True(arena.Keys.TryResolve(key: out var secret, name: CellName.Parse(candidate: "secret")));
        FieldCellDelta[] changed = [new FieldCellDelta(Cell: -1, Field: 0, Raw: 0, Row: hand.Ordinal)];
        var disclosure = new WorldStateChangeDisclosure();
        var seat2 = Principal.Seat(slot: 1);
        int Replaced() => disclosure.Filter(server: server, recipient: seat2, changes: changed, rows: true).Span.ToArray()
            .Count(predicate: delta => (delta.Row == hand.Ordinal) && (delta.Cell == -1));

        Assert.Equal(expected: 1, actual: Replaced());
        Assert.True(arena.TryWrite(rowOrdinal: hand.Ordinal, key: secret, value: CellValue.Int(value: 8), reason: out var reason), reason);
        Assert.Equal(expected: 0, actual: Replaced());
        Assert.True(arena.TryWrite(rowOrdinal: hand.Ordinal, key: pub, value: CellValue.Int(value: 4), reason: out reason), reason);
        Assert.Equal(expected: 1, actual: Replaced());
        disclosure.Forget();
        Assert.Equal(expected: 1, actual: Replaced());
    }
}
