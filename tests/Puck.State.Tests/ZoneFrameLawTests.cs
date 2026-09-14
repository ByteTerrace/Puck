using Xunit;

namespace Puck.State.Tests;

/// <summary>An ordered zone inside a frame: membership loads in pile order, a transfer moves the selected token
/// between zones without minting a row or touching the section, every store-based read (a key, a position, a
/// count, an arrangement rank) sees the moved membership, and the layout survives a membership change.</summary>
public sealed class ZoneFrameLawTests {
    private sealed class ObservedCells(StateCell[] cells) : IReadOnlyList<StateCell> {
        public int Reads { get; private set; }
        public int Count => cells.Length;
        public StateCell this[int index] {
            get {
                Reads++;
                return cells[index];
            }
        }
        public IEnumerator<StateCell> GetEnumerator() => ((IEnumerable<StateCell>)cells).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
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
    public void RebindRefreshesDomainKeysAndOrderWithoutChangingTheLayout() {
        var (layout, frame, rows) = Build(deck: "cab");
        var rebound = new List<StateRow>(collection: rows) {
            [0] = rows[0] with { Cells = Members(keys: "dcbx") },
            [1] = rows[1] with { Cells = Members(keys: "xbc") },
        };
        Assert.True(condition: layout.Fits(rows: rebound));
        frame.Rebind(rows: rebound);
        frame.Load(source: new RowStore(rows: rebound));
        Assert.True(condition: frame.TryZoneOrdinals(rowOrdinal: 1, ordinals: out var ordinals));
        Assert.Equal(expected: new long[] { 3, 2, 1 }, actual: ordinals.ToArray());
        Assert.Equal(expected: 5L, actual: StateReader.ArrangementRank(store: frame, zone: rebound[1], rowOrdinal: 1));
        Assert.False(condition: frame.TryStored(rowOrdinal: 1, key: Name(value: "a"), value: out _, text: out _));
        Assert.True(condition: frame.TryTransferToken(from: rebound[1], to: rebound[2], key: Name(value: "x"), insertFirst: false, reason: out _));
        Assert.Equal(expected: "x", actual: Order(frame: frame, zone: rebound[2]));

        // A second frame shares shape, never the first frame's key cache.
        var other = new StateFrame(layout: layout, rows: rows);
        other.Load(source: new RowStore(rows: rows));
        Assert.Equal(expected: "cab", actual: Order(frame: other, zone: rows[1]));
        Assert.Equal(expected: 4L, actual: StateReader.ArrangementRank(store: other, zone: rows[1]));
    }
    [Fact]
    public void UnvalidatedZonesIntentionallyRankRetainedFrameMembersWhileTheDocumentRefusesUnknownKeys() {
        var (layout, _, rows) = Build(deck: "axb");
        var duplicate = new List<StateRow>(collection: rows) {
            [0] = rows[0] with { Cells = Members(keys: "abac") },
        };
        var frame = new StateFrame(layout: layout, rows: duplicate);
        frame.Load(source: new RowStore(rows: duplicate));
        Assert.True(condition: frame.TryZoneOrdinals(rowOrdinal: 1, ordinals: out var ordinals));
        Assert.Equal(expected: new long[] { 0, 1 }, actual: ordinals.ToArray());
        Assert.Equal(expected: "ab", actual: Order(frame: frame, zone: duplicate[1]));
        // Preserve the existing store disagreement only for invalid input: admission rejects the unknown key.
        Assert.Equal(expected: 0L, actual: StateReader.ArrangementRank(store: frame, zone: duplicate[1]));
        Assert.Equal(expected: -1L, actual: StateReader.ArrangementRank(rows: duplicate, zone: duplicate[1]));
        Assert.False(condition: frame.TryZoneOrdinals(rowOrdinal: 0, ordinals: out _));
        Assert.False(condition: frame.TryZoneOrdinals(rowOrdinal: -1, ordinals: out _));
        Assert.False(condition: frame.TryZoneOrdinals(rowOrdinal: rows.Count, ordinals: out _));
    }
    [Fact]
    public void ArrangementRankIgnoresAnOrdinalNamingAnotherZoneOfTheSameDomain() {
        var (_, frame, rows) = Build(deck: "cba", hand: "d");
        Assert.Equal(expected: 0L, actual: StateReader.ArrangementRank(store: frame, zone: rows[2], rowOrdinal: 2));
        Assert.Equal(expected: 5L, actual: StateReader.ArrangementRank(store: frame, zone: rows[1], rowOrdinal: 2));
        Assert.Equal(expected: 5L, actual: StateReader.ArrangementRank(store: frame, zone: rows[1], rowOrdinal: rows.Count));
    }
    [Fact]
    public void WarmZoneReadsTransfersAndRanksDoNotRevisitDomainCells() {
        var (layout, _, rows) = Build();
        var cells = new ObservedCells(cells: Members(keys: "abcd"));
        var observed = new List<StateRow>(collection: rows) { [0] = rows[0] with { Cells = cells } };
        var frame = new StateFrame(layout: layout, rows: observed);
        frame.Load(source: new RowStore(rows: observed));
        var before = cells.Reads;
        var mark = frame.BeginJournalScope();
        Assert.True(condition: frame.TryTransferToken(from: observed[1], to: observed[2], key: Name(value: "b"), insertFirst: false, reason: out _));
        Assert.True(condition: frame.TryStored(rowOrdinal: 2, key: Name(value: "b"), value: out _, text: out _));
        Assert.Equal(expected: 0L, actual: StateReader.ArrangementRank(store: frame, zone: observed[1], rowOrdinal: 1));
        frame.RewindJournalScope(mark: mark);
        Assert.True(condition: frame.TryStored(rowOrdinal: 1, key: Name(value: "b"), value: out _, text: out _));
        Assert.Equal(expected: before, actual: cells.Reads);
    }
    [Fact]
    public void WarmDomainCachesRebindAndReloadWithoutAllocation() {
        var (_, frame, rows) = Build();
        var store = new RowStore(rows: rows);
        var key = Name(value: "b");
        for (var iteration = 0; (iteration < 100); iteration++) {
            frame.Rebind(rows: rows);
            frame.Load(source: store);
            _ = frame.TryStored(key: key, rowOrdinal: 1, text: out _, value: out _);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; (iteration < 100); iteration++) {
            frame.Rebind(rows: rows);
            frame.Load(source: store);
            _ = frame.TryStored(key: key, rowOrdinal: 1, text: out _, value: out _);
        }
        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - before));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(21)]
    public void FrameArrangementRankMatchesTheDocumentAtItsSizeBoundary(int count) {
        var keys = "abcdefghijklmnopqrstu"[..count];
        var domain = new StateRow(Name: Name(value: "tokens"), Kind: CellKind.Bool, Capacity: 21, Cells: Members(keys: keys));
        var zone = new StateRow(Name: Name(value: "pile"), Kind: CellKind.Bool, Capacity: 21,
            Domain: new StateDomain.KeysOf(Row: domain.Name, Ordered: true), Cells: Members(keys: new string(value: keys.Reverse().ToArray())));
        StateRow[] rows = [domain, zone];
        var frame = new StateFrame(layout: new FrameLayout(rows: rows, topology: static _ => null), rows: rows);
        frame.Load(source: new RowStore(rows: rows));
        Assert.Equal(expected: StateReader.ArrangementRank(rows: rows, zone: zone), actual: StateReader.ArrangementRank(store: frame, zone: zone, rowOrdinal: 1));
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
                tick: 1UL,
                engineTick: 1UL
            )
        );
        Assert.Equal(
            2L,
            StateReader.ReduceRaw(
                op: StateReduceOp.Count,
                row: hand,
                store: frame,
                tick: 1UL,
                engineTick: 1UL
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
