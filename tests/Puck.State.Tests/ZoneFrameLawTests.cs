using Xunit;

namespace Puck.State.Tests;

/// <summary>An ordered zone inside a frame: membership loads in pile order, a transfer moves the selected token
/// between zones without minting a row or touching the section, every store-based read (a key, a position, a
/// count, an arrangement rank) sees the moved membership, and the layout survives a membership change.</summary>
public sealed class ZoneFrameLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell[] Members(string keys) => [.. keys.Select(static key => new StateCell(Key: CellName.Parse(candidate: key.ToString()), Value: 1L))];

    private static (FrameLayout Layout, StateFrame Frame, List<StateRow> Rows) Build(string deck = "abc", string hand = "", int handCapacity = 2) {
        var cards = new StateRow(Name: Name("cards"), Kind: CellKind.Bool, Capacity: 4, Cells: Members("abcd"));
        var zone = new StateDomain.KeysOf(Name("cards"), Ordered: true);
        var rows = new List<StateRow> {
            cards,
            new(Name: Name("deck"), Kind: CellKind.Bool, Capacity: 4, Domain: zone, Cells: Members(deck)),
            new(Name: Name("hand"), Kind: CellKind.Bool, Capacity: handCapacity, Domain: zone, Cells: Members(hand)),
            new(Name: Name("table"), Kind: CellKind.Bool, Capacity: 4, Domain: zone, Cells: Members("d")),
        };
        var layout = new FrameLayout(rows: rows, topology: static _ => null);
        var frame = new StateFrame(layout: layout, rows: rows);

        frame.Load(source: new RowStore(rows: rows));

        return (layout, frame, rows);
    }

    private static string Order(StateFrame frame, StateRow zone) {
        var keys = new List<string>();

        for (var index = 0; index < frame.CellCount(row: zone); index++) {
            Assert.True(frame.TryKeyAt(row: zone, index: index, key: out var key));
            keys.Add(key.Value);
        }

        return string.Concat(keys);
    }

    [Fact]
    public void AZoneLaysOutByItsCapacityAndLoadsItsMembersInPileOrder() {
        var (layout, frame, rows) = Build();

        Assert.True(layout.TryOrdinal(name: "deck", ordinal: out var deck));
        Assert.True(layout.TryOrdinal(name: "hand", ordinal: out var hand));
        Assert.Equal(FrameRowKind.Zone, layout[deck].Kind);
        // Count, then four member ordinals, then four values: the smaller of the declared capacity and the domain.
        Assert.Equal(9, layout[deck].Length);
        Assert.Equal(4, layout[deck].ZoneCapacity);
        Assert.Equal(5, layout[hand].Length);
        Assert.Equal(0, layout[deck].DomainOrdinal);

        Assert.Equal("abc", Order(frame, rows[1]));
        Assert.Equal(3, frame.CellCount(row: rows[1]));
        Assert.Equal(0, frame.CellCount(row: rows[2]));
        Assert.True(frame.TryStored(row: rows[1], key: Name("b"), value: out var b, text: out _));
        Assert.Equal(1L, b);
        Assert.False(frame.TryStored(row: rows[1], key: Name("d"), value: out _, text: out _));
        Assert.True(frame.TryStoredAt(row: rows[1], index: 2, value: out _));
        Assert.False(frame.TryStoredAt(row: rows[1], index: 3, value: out _));
    }

    [Fact]
    public void ATransferMovesTheTokenOntoTheDestinationsTopAndEveryStoreReadSeesTheNewMembership() {
        var (_, frame, rows) = Build();
        var deck = rows[1];
        var hand = rows[2];

        Assert.True(frame.TryTransferToken(from: deck, to: hand, key: Name("c"), insertFirst: false, reason: out var reason), reason);
        Assert.True(frame.TryTransferToken(from: deck, to: hand, key: Name("a"), insertFirst: false, reason: out reason), reason);

        Assert.Equal("b", Order(frame, deck));
        Assert.Equal("ca", Order(frame, hand));
        Assert.Equal(1L, StateReader.ReduceRaw(store: frame, row: deck, op: StateReduceOp.Count, tick: 1UL));
        Assert.Equal(2L, StateReader.ReduceRaw(store: frame, row: hand, op: StateReduceOp.Count, tick: 1UL));
        // c before a is the one inversion of two tokens: rank 1 of 2!, read through the frame, not the section.
        Assert.Equal(1L, StateReader.ArrangementRank(store: frame, zone: hand));
        Assert.Equal(0L, StateReader.ArrangementRank(rows: rows, zone: deck));
        Assert.True(frame.TryStored(row: hand, key: Name("a"), value: out _, text: out _));
        Assert.False(frame.TryStored(row: deck, key: Name("a"), value: out _, text: out _));

        // The section the frame loaded from never moved.
        Assert.Equal(3, deck.Cells!.Count);
        Assert.Empty(hand.Cells!);
    }

    [Fact]
    public void ATransferRefusesANonMemberAFullDestinationAndADuplicate() {
        var (_, frame, rows) = Build(hand: "d", handCapacity: 2);
        var deck = rows[1];
        var hand = rows[2];
        var table = rows[3];

        Assert.False(frame.TryTransferToken(from: deck, to: hand, key: Name("d"), insertFirst: false, reason: out var reason));
        Assert.Contains("does not contain", reason);
        // d stands on the table and in the hand: the destination already holds it.
        Assert.False(frame.TryTransferToken(from: table, to: hand, key: Name("d"), insertFirst: false, reason: out reason));
        Assert.Contains("already contains", reason);
        Assert.True(frame.TryTransferToken(from: deck, to: hand, key: Name("a"), insertFirst: false, reason: out reason), reason);
        Assert.False(frame.TryTransferToken(from: deck, to: hand, key: Name("b"), insertFirst: false, reason: out reason));
        Assert.Contains("full", reason);
        Assert.Equal("da", Order(frame, hand));
    }

    [Fact]
    public void TheTransformSelectsFirstOrLastForACountAndNeverDraws() {
        var (_, frame, rows) = Build(handCapacity: 4);
        var deck = rows[1];
        var hand = rows[2];

        Assert.True(frame.TryTransfer(transfer: new StateTransform.Transfer(From: "deck", To: "hand", Selector: ZoneSelector.Last, Count: 2), reason: out var reason), reason);
        Assert.Equal("a", Order(frame, deck));
        Assert.Equal("cb", Order(frame, hand));
        Assert.True(frame.TryTransfer(transfer: new StateTransform.Transfer(From: "hand", To: "deck", Selector: ZoneSelector.First, InsertFirst: true), reason: out reason), reason);
        Assert.Equal("ca", Order(frame, deck));
        Assert.True(frame.TryTransfer(transfer: new StateTransform.Transfer(From: "deck", To: "hand", Selector: ZoneSelector.Key, Key: "a"), reason: out reason), reason);
        Assert.Equal("ba", Order(frame, hand));
        Assert.False(frame.TryTransfer(transfer: new StateTransform.Transfer(From: "deck", To: "hand", Selector: ZoneSelector.Random, Draw: "draw"), reason: out reason));
        Assert.Contains("never draws", reason);
    }

    [Fact]
    public void AMembershipChangeInTheSectionStillFitsTheLayoutAndAWriteReachesMembersAlone() {
        var (layout, frame, rows) = Build();
        var moved = new List<StateRow>(rows) {
            [1] = rows[1] with { Cells = Members("ab") },
            [2] = rows[2] with { Cells = Members("c") },
        };

        Assert.True(layout.Fits(rows: moved));
        Assert.False(layout.Fits(rows: new List<StateRow>(rows) { [0] = rows[0] with { Cells = Members("abcde") } }));

        Assert.True(frame.TryWrite(row: rows[1], key: Name("b"), value: 0L, write: StateWriteKind.Set, reason: out var reason), reason);
        Assert.True(frame.TryStored(row: rows[1], key: Name("b"), value: out var b, text: out _));
        Assert.Equal(0L, b);
        Assert.False(frame.TryWrite(row: rows[1], key: Name("d"), value: 1L, write: StateWriteKind.Set, reason: out reason));
        Assert.Contains("never mints", reason);
    }
}
