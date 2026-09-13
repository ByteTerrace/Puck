using Xunit;

namespace Puck.State.Tests;

/// <summary>An ordered zone inside a frame: membership loads in pile order, a transfer moves the selected token
/// between zones without minting a row or touching the section, every store-based read (a key, a position, a
/// count, an arrangement rank) sees the moved membership, and the layout survives a membership change.</summary>
public sealed class ZoneFrameLawTests {
    private static (FrameLayout Layout, StateFrame Frame, List<StateRow> Rows) Build(string deck = "abc", string hand = "", int handCapacity = 2) {
        var cards = new StateRow(
            Name: Name(value: "cards"),
            Kind: CellKind.Bool,
            Capacity: 4,
            Cells: Members(keys: "abcd")
        );
        var zone = new StateDomain.KeysOf(
            Name(value: "cards"),
            Ordered: true
        );
        var rows = new List<StateRow> {
            cards,
            new(
            Name: Name(value: "deck"),
            Kind: CellKind.Bool,
            Capacity: 4,
            Domain: zone,
            Cells: Members(keys: deck)
        ),
            new(
            Name: Name(value: "hand"),
            Kind: CellKind.Bool,
            Capacity: handCapacity,
            Domain: zone,
            Cells: Members(keys: hand)
        ),
            new(
            Name: Name(value: "table"),
            Kind: CellKind.Bool,
            Capacity: 4,
            Domain: zone,
            Cells: Members(keys: "d")
        ),
        };
        var layout = new FrameLayout(
            rows: rows,
            topology: static _ => null
        );
        var frame = new StateFrame(
            layout: layout,
            rows: rows
        );

        frame.Load(source: new RowStore(rows: rows));

        return (layout, frame, rows);
    }
    private static StateCell[] Members(string keys) => [.. keys.Select(selector: static key => new StateCell(
            Key: CellName.Parse(candidate: key.ToString()),
            Value: 1L
        ))];
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static string Order(StateFrame frame, StateRow zone) {
        var keys = new List<string>();

        for (var index = 0; (index < frame.CellCount(row: zone)); index++) {
            Assert.True(condition: frame.TryKeyAt(
                index: index,
                key: out var key,
                row: zone
            ));
            keys.Add(item: key.Value);
        }

        return string.Concat(values: keys);
    }

    [Fact]
    public void AMembershipChangeInTheSectionStillFitsTheLayoutAndAWriteReachesMembersAlone() {
        var (layout, frame, rows) = Build();
        var moved = new List<StateRow>(collection: rows) {
            [1] = rows[1] with { Cells = Members(keys: "ab") },
            [2] = rows[2] with { Cells = Members(keys: "c") },
        };

        Assert.True(condition: layout.Fits(rows: moved));
        Assert.False(condition: layout.Fits(rows: new List<StateRow>(collection: rows) { [0] = rows[0] with { Cells = Members(keys: "abcde") } }));

        Assert.True(
            condition: frame.TryWrite(
                row: rows[1],
                key: Name(value: "b"),
                value: 0L,
                write: StateWriteKind.Set,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(condition: frame.TryStored(
            row: rows[1],
            key: Name(value: "b"),
            value: out var b,
            text: out _
        ));
        Assert.Equal(
            actual: b,
            expected: 0L
        );
        Assert.False(condition: frame.TryWrite(
            row: rows[1],
            key: Name(value: "d"),
            value: 1L,
            write: StateWriteKind.Set,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "never mints"
        );
    }
    [Fact]
    public void AReductionFilterTracksTransferredMembership() {
        var (_, frame, rows) = Build();
        Assert.Equal(
            3,
            StateReader.ReduceRaw(
                frame,
                rows[0],
                StateReduceOp.Count,
                0,
                rows[1],
                null
            )
        );
        Assert.True(condition: frame.TryTransferToken(
            rows[1],
            rows[2],
            Name(value: "a"),
            false,
            out _
        ));
        Assert.Equal(
            2,
            StateReader.ReduceRaw(
                frame,
                rows[0],
                StateReduceOp.Count,
                0,
                rows[1],
                null
            )
        );
        Assert.Equal(
            1,
            StateReader.ReduceRaw(
                frame,
                rows[0],
                StateReduceOp.Count,
                0,
                rows[2],
                null
            )
        );
    }
    [Fact]
    public void ATransferMovesTheTokenOntoTheDestinationsTopAndEveryStoreReadSeesTheNewMembership() {
        var (_, frame, rows) = Build();
        var deck = rows[1];
        var hand = rows[2];

        Assert.True(
            condition: frame.TryTransferToken(
                from: deck,
                to: hand,
                key: Name(value: "c"),
                insertFirst: false,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: frame.TryTransferToken(
                from: deck,
                to: hand,
                key: Name(value: "a"),
                insertFirst: false,
                reason: out reason
            ),
            userMessage: reason
        );

        Assert.Equal(
            "b",
            Order(
                frame: frame,
                zone: deck
            )
        );
        Assert.Equal(
            "ca",
            Order(
                frame: frame,
                zone: hand
            )
        );
        Assert.Equal(
            1L,
            StateReader.ReduceRaw(
                op: StateReduceOp.Count,
                row: deck,
                store: frame,
                tick: 1UL
            )
        );
        Assert.Equal(
            2L,
            StateReader.ReduceRaw(
                op: StateReduceOp.Count,
                row: hand,
                store: frame,
                tick: 1UL
            )
        );
        // c before a is the one inversion of two tokens: rank 1 of 2!, read through the frame, not the section.
        Assert.Equal(
            1L,
            StateReader.ArrangementRank(
                store: frame,
                zone: hand
            )
        );
        Assert.Equal(
            0L,
            StateReader.ArrangementRank(
                rows: rows,
                zone: deck
            )
        );
        Assert.True(condition: frame.TryStored(
            row: hand,
            key: Name(value: "a"),
            value: out _,
            text: out _
        ));
        Assert.False(condition: frame.TryStored(
            row: deck,
            key: Name(value: "a"),
            value: out _,
            text: out _
        ));

        // The section the frame loaded from never moved.
        Assert.Equal(
            3,
            deck.Cells!.Count
        );
        Assert.Empty(collection: hand.Cells!);
    }
    [Fact]
    public void ATransferRefusesANonMemberAFullDestinationAndADuplicate() {
        var (_, frame, rows) = Build(
            hand: "d",
            handCapacity: 2
        );
        var deck = rows[1];
        var hand = rows[2];
        var table = rows[3];

        Assert.False(condition: frame.TryTransferToken(
            from: deck,
            to: hand,
            key: Name(value: "d"),
            insertFirst: false,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "does not contain"
        );
        // d stands on the table and in the hand: the destination already holds it.
        Assert.False(condition: frame.TryTransferToken(
            from: table,
            to: hand,
            key: Name(value: "d"),
            insertFirst: false,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "already contains"
        );
        Assert.True(
            condition: frame.TryTransferToken(
                from: deck,
                to: hand,
                key: Name(value: "a"),
                insertFirst: false,
                reason: out reason
            ),
            userMessage: reason
        );
        Assert.False(condition: frame.TryTransferToken(
            from: deck,
            to: hand,
            key: Name(value: "b"),
            insertFirst: false,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "full"
        );
        Assert.Equal(
            "da",
            Order(
                frame: frame,
                zone: hand
            )
        );
    }
    [Fact]
    public void AZoneLaysOutByItsCapacityAndLoadsItsMembersInPileOrder() {
        var (layout, frame, rows) = Build();

        Assert.True(condition: layout.TryOrdinal(
            name: "deck",
            ordinal: out var deck
        ));
        Assert.True(condition: layout.TryOrdinal(
            name: "hand",
            ordinal: out var hand
        ));
        Assert.Equal(
            FrameRowKind.Zone,
            layout[deck].Kind
        );
        // Count, then four member ordinals, then four values: the smaller of the declared capacity and the domain.
        Assert.Equal(
            9,
            layout[deck].Length
        );
        Assert.Equal(
            4,
            layout[deck].ZoneCapacity
        );
        Assert.Equal(
            5,
            layout[hand].Length
        );
        Assert.Equal(
            0,
            layout[deck].DomainOrdinal
        );

        Assert.Equal(
            "abc",
            Order(
                frame: frame,
                zone: rows[1]
            )
        );
        Assert.Equal(
            3,
            frame.CellCount(row: rows[1])
        );
        Assert.Equal(
            0,
            frame.CellCount(row: rows[2])
        );
        Assert.True(condition: frame.TryStored(
            row: rows[1],
            key: Name(value: "b"),
            value: out var b,
            text: out _
        ));
        Assert.Equal(
            actual: b,
            expected: 1L
        );
        Assert.False(condition: frame.TryStored(
            row: rows[1],
            key: Name(value: "d"),
            value: out _,
            text: out _
        ));
        Assert.True(condition: frame.TryStoredAt(
            row: rows[1],
            index: 2,
            value: out _
        ));
        Assert.False(condition: frame.TryStoredAt(
            row: rows[1],
            index: 3,
            value: out _
        ));
    }
    [Fact]
    public void TheTransformSelectsFirstOrLastForACountAndNeverDraws() {
        var (_, frame, rows) = Build(handCapacity: 4);
        var deck = rows[1];
        var hand = rows[2];

        Assert.True(
            condition: frame.TryTransfer(
                transfer: new StateTransform.Transfer(
                    From: "deck",
                    To: "hand",
                    Selector: ZoneSelector.Last,
                    Count: 2
                ),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            "a",
            Order(
                frame: frame,
                zone: deck
            )
        );
        Assert.Equal(
            "cb",
            Order(
                frame: frame,
                zone: hand
            )
        );
        Assert.True(
            condition: frame.TryTransfer(
                transfer: new StateTransform.Transfer(
                    From: "hand",
                    To: "deck",
                    Selector: ZoneSelector.First,
                    InsertFirst: true
                ),
                reason: out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            "ca",
            Order(
                frame: frame,
                zone: deck
            )
        );
        Assert.True(
            condition: frame.TryTransfer(
                transfer: new StateTransform.Transfer(
                    From: "deck",
                    To: "hand",
                    Selector: ZoneSelector.Key,
                    Key: "a"
                ),
                reason: out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            "ba",
            Order(
                frame: frame,
                zone: hand
            )
        );
        Assert.False(condition: frame.TryTransfer(
            transfer: new StateTransform.Transfer(
                From: "deck",
                To: "hand",
                Selector: ZoneSelector.Random,
                Draw: "draw"
            ),
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "never draws"
        );
    }
}
