using Puck.World.Protocol;
using Xunit;
using static Puck.World.Tests.SolitaireFixtures;

namespace Puck.World.Tests;

/// <summary>The shipped patience programs run through the ordinary rule and mutation pipeline.</summary>
public sealed class SolitaireLawTests {
    [Fact]
    public void AHiddenOrBrokenRunCannotMove() {
        foreach (var hidden in new[] { true, false }) {
            using var f = Fixtures.FreshServer(Position(
                "solitaireKlondike",
                new() { [2] = (hidden
                ? [4, 16]
                : [4, 3]), [3] = [18] },
                hidden: (hidden
                ? [4]
                : []),
                card: 4
            ));

            Settle(f: f);
            Assert.Equal(
                -1,
                Value(
                    f: f,
                    game: "solitaireKlondike",
                    key: "result"
                )
            );
            Assert.Equal(
                2,
                Count(
                    f: f,
                    game: "solitaireKlondike",
                    pile: 2
                )
            );
        }
    }
    [Fact]
    public void AllSolitaireModulesComposeWithTheShippedNexus() {
        var definition = AuthoredGameFixtures.Nexus;

        Assert.Contains(
            collection: definition!.State,
            filter: row => (row.Name.Value == "solitaireFreecell")
        );
        Assert.Contains(
            collection: definition.State,
            filter: row => (row.Name.Value == "solitaireSpider")
        );
        Assert.Contains(
            collection: definition.State,
            filter: row => (row.Name.Value == "solitaireKlondike")
        );
        Assert.DoesNotContain(
            collection: definition.State.Single(predicate: row => (row.Name.Value == "spider")).Cells!,
            filter: cell => (cell.Key.Value == "request")
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnEmptyFreeCellDestinationIsNotAdditionalWorkspace(bool spare) {
        var piles = new Dictionary<int, int[]> { [2] = [4, 16, 2], [4] = [30], [5] = [31], [6] = [32], [7] = [33], [8] = [34], [14] = [40], [15] = [41], [16] = [42] };

        if (!spare) { piles[9] = [35]; }
        using var f = Fixtures.FreshServer(Position(
            "solitaireFreecell",
            piles,
            card: 4
        ));

        Settle(
            f: f,
            game: "solitaireFreecell"
        );
        Assert.Equal(
            (spare
            ? 1
            : -1),
            Value(
                f: f,
                game: "solitaireFreecell",
                key: "result"
            )
        );
    }
    [InlineData("solitaireKlondike")]
    [InlineData("solitaireFreecell")]
    [Theory]
    public void FoundationsRequireSuitAndAscendingRank(string game) {
        using var f = Fixtures.FreshServer(Position(
            game,
            new() { [2] = [0], [3] = [1], [4] = [13] },
            to: 10
        ));

        Settle(f: f);
        Assert.Equal(
            1,
            Value(
                f: f,
                game: game,
                key: "result"
            )
        );
        Request(
            action: 2,
            card: 13,
            f: f,
            from: 4,
            game: game,
            to: 10
        );
        Assert.Equal(
            -1,
            Value(
                f: f,
                game: game,
                key: "result"
            )
        );
        Request(
            action: 2,
            card: 1,
            f: f,
            from: 3,
            game: game,
            to: 10
        );
        Assert.Equal(
            1,
            Value(
                f: f,
                game: game,
                key: "result"
            )
        );
        Assert.Equal(
            new[] { "0", "1" },
            Row(
                f: f,
                name: (game + "Pile10")
            ).Cells!.Select(selector: c => c.Key.Value)
        );
    }
    [InlineData(false, false)]
    [InlineData(true, true)]
    [Theory]
    public void FreeCellSupermovesAreBoundedByAvailableWorkspace(bool spareColumn, bool legal) {
        var piles = new Dictionary<int, int[]> { [2] = [4, 16, 2], [3] = [18], [4] = [30], [5] = [31], [6] = [32], [7] = [33], [8] = [34], [14] = [40], [15] = [41] };

        if (!spareColumn) { piles[9] = [35]; }
        // One further card makes only one free cell available: capacity two, or four with a spare column.
        piles[16] = [42];
        using var f = Fixtures.FreshServer(Position(
            "solitaireFreecell",
            piles,
            card: 4
        ));

        Settle(f: f);
        Assert.Equal(
            (legal
            ? 1
            : -1),
            Value(
                f: f,
                game: "solitaireFreecell",
                key: "result"
            )
        );
    }
    [Fact]
    public void FreeCellsHoldOneCardAndFoundationMovesCannotBeUndoneAsTableauMoves() {
        using var f = Fixtures.FreshServer(Position(
            "solitaireFreecell",
            new() { [2] = [0], [3] = [1] },
            to: 14
        ));

        Settle(f: f);
        Assert.Equal(
            1,
            Value(
                f: f,
                game: "solitaireFreecell",
                key: "result"
            )
        );
        Request(
            action: 2,
            card: 1,
            f: f,
            from: 3,
            game: "solitaireFreecell",
            to: 14
        );
        Assert.Equal(
            -1,
            Value(
                f: f,
                game: "solitaireFreecell",
                key: "result"
            )
        );
        Request(
            action: 2,
            card: 0,
            f: f,
            from: 14,
            game: "solitaireFreecell",
            to: 10
        );
        Assert.Equal(
            1,
            Value(
                f: f,
                game: "solitaireFreecell",
                key: "result"
            )
        );
        Request(
            action: 2,
            card: 0,
            f: f,
            from: 10,
            game: "solitaireFreecell",
            to: 4
        );
        Assert.Equal(
            -1,
            Value(
                f: f,
                game: "solitaireFreecell",
                key: "result"
            )
        );
    }
    [InlineData(99, 3, 4)]
    [InlineData(2, 99, 4)]
    [InlineData(2, 2, 4)]
    [InlineData(2, 3, -1)]
    [InlineData(2, 3, 999)]
    [Theory]
    public void InvalidMoveAddressesLeavePilesAndMoveCountUntouched(int from, int to, int card) {
        using var f = Fixtures.FreshServer(Position(
            "solitaireKlondike",
            new() { [2] = [4], [3] = [18] },
            from: from,
            to: to,
            card: card
        ));

        Settle(
            f: f,
            game: "solitaireKlondike"
        );
        Assert.Equal(
            -1,
            Value(
                f: f,
                game: "solitaireKlondike",
                key: "result"
            )
        );
        Assert.Equal(
            0,
            Value(
                f: f,
                game: "solitaireKlondike",
                key: "moves"
            )
        );
        Assert.Equal(
            "4",
            Assert.Single(collection: Row(
                f: f,
                name: "solitaireKlondikePile2"
            ).Cells!).Key.Value
        );
        Assert.Equal(
            "18",
            Assert.Single(collection: Row(
                f: f,
                name: "solitaireKlondikePile3"
            ).Cells!).Key.Value
        );
    }
    [Theory]
    [InlineData(3, 17, true)] // black 4 onto red 5
    [InlineData(3, 4, false)] // same colour
    [InlineData(3, 18, false)] // wrong rank
    public void KlondikeJudgesRankAndColourBeforeTransferring(int card, int target, bool legal) {
        using var f = Fixtures.FreshServer(Position(
            "solitaireKlondike",
            new() { [2] = [card], [3] = [target] },
            card: card
        ));

        Settle(f: f);
        Assert.Equal(
            (legal
            ? 1
            : -1),
            Value(
                f: f,
                game: "solitaireKlondike",
                key: "result"
            )
        );
        Assert.Equal(
            (legal
            ? 0
            : 1),
            Count(
                f: f,
                game: "solitaireKlondike",
                pile: 2
            )
        );
        Assert.Equal(
            (legal
            ? new[] { target.ToString(), card.ToString() }
            : [target.ToString()]),
            Row(
                f: f,
                name: "solitaireKlondikePile3"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
    }
    [Fact]
    public void KlondikeMovesAnEntireAlternatingRunAndRevealsTheCoveredCard() {
        using var f = Fixtures.FreshServer(Position(
            "solitaireKlondike",
            new() { [2] = [40, 4, 16, 2], [3] = [18] },
            hidden: [40],
            card: 4
        ));

        Settle(f: f);
        Assert.Equal(
            1,
            Value(
                f: f,
                game: "solitaireKlondike",
                key: "result"
            )
        );
        Assert.Equal(
            new[] { "18", "4", "16", "2" },
            Row(
                f: f,
                name: "solitaireKlondikePile3"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        Assert.Equal(
            1,
            Row(
                f: f,
                name: "solitaireKlondikeFace"
            ).Cells!.Single(predicate: c => (c.Key.Value == "40")).Value
        );
    }
    [InlineData(12, true)]
    [InlineData(11, false)]
    [Theory]
    public void OnlyKingsStartEmptyKlondikeColumns(int card, bool legal) {
        using var f = Fixtures.FreshServer(Position(
            "solitaireKlondike",
            new() { [2] = [card] },
            card: card
        ));

        Settle(f: f);
        Assert.Equal(
            (legal
            ? 1
            : -1),
            Value(
                f: f,
                game: "solitaireKlondike",
                key: "result"
            )
        );
    }
    [Fact]
    public void SpiderClearsCompleteSuitRunsAndCountsTheWin() {
        var piles = new Dictionary<int, int[]> { [2] = Enumerable.Range(
            count: 13,
            start: 0
        ).Reverse().ToArray(), [12] = Enumerable.Range(
            count: 91,
            start: 13
        ).ToArray() };
        using var f = Fixtures.FreshServer(Position(
            "solitaireSpider",
            piles,
            action: 0
        ));

        Settle(f: f);
        Assert.Equal(
            104,
            Count(
                f: f,
                game: "solitaireSpider",
                pile: 12
            )
        );
        Assert.Equal(
            2,
            Value(
                f: f,
                game: "solitaireSpider",
                key: "status"
            )
        );
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void SpiderDoesNotClearHiddenOrMixedSuitKingToAceColumns(bool hidden) {
        var run = Enumerable.Range(
            count: 13,
            start: 0
        ).Reverse().ToArray();

        if (!hidden) { run[^1] = 13; }
        using var f = Fixtures.FreshServer(Position(
            "solitaireSpider",
            new() { [2] = run },
            hidden: (hidden
            ? [5]
            : []),
            action: 0
        ));

        Settle(
            f: f,
            game: "solitaireSpider"
        );
        Assert.Equal(
            13,
            Count(
                f: f,
                game: "solitaireSpider",
                pile: 2
            )
        );
        Assert.Equal(
            0,
            Count(
                f: f,
                game: "solitaireSpider",
                pile: 12
            )
        );
    }
    [Fact]
    public void SpiderLocksItsSuitCountWhenTheDealIsAccepted() {
        using var f = Fixtures.FreshServer(Game(
            "solitaireSpider",
            source => {
            Set(
                game: "solitaireSpider",
                key: "option",
                source: source,
                value: 4
            );
            Set(
                game: "solitaireSpider",
                key: "action",
                source: source,
                value: 1
            );
            Set(
                game: "solitaireSpider",
                key: "request",
                source: source,
                value: 1
            );
        }
        ));

        Steps(
            f: f,
            n: 2
        );
        f.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "solitaireSpider",
            Value: 1,
            Key: "option",
            Kind: WorldDocumentWriteKind.Set
        ));
        Settle(
            f: f,
            game: "solitaireSpider"
        );
        Assert.Equal(
            4,
            Value(
                f: f,
                game: "solitaireSpider",
                key: "activeOption"
            )
        );
        Assert.Equal(
            4,
            Row(
                f: f,
                name: "solitaireSpiderSuit"
            ).Cells!.Select(selector: c => c.Value).Distinct().Count()
        );
        Assert.Equal(
            1,
            Value(
                f: f,
                game: "solitaireSpider",
                key: "option"
            )
        );
    }
    [Fact]
    public void SpiderMayBuildAcrossSuitsButOnlyMovesSameSuitRuns() {
        using var f = Fixtures.FreshServer(Position(
            "solitaireSpider",
            new() { [2] = [4], [3] = [18] },
            card: 4
        ));

        Settle(f: f);
        Assert.Equal(
            1,
            Value(
                f: f,
                game: "solitaireSpider",
                key: "result"
            )
        );
        Request(
            action: 2,
            card: 18,
            f: f,
            from: 3,
            game: "solitaireSpider",
            to: 4
        );
        Assert.Equal(
            -1,
            Value(
                f: f,
                game: "solitaireSpider",
                key: "result"
            )
        );
        Request(
            action: 2,
            card: 4,
            f: f,
            from: 3,
            game: "solitaireSpider",
            to: 4
        );
        Assert.Equal(
            1,
            Value(
                f: f,
                game: "solitaireSpider",
                key: "result"
            )
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void SpiderRequiresEveryColumnToBeOccupiedBeforeDealing(bool allOccupied) {
        var piles = Enumerable.Range(
            count: (allOccupied
            ? 10
            : 9),
            start: 2
        ).ToDictionary(
            elementSelector: z => new[] { (z - 2) },
            keySelector: z => z
        );
        using var f = Fixtures.FreshServer(Position(
            "solitaireSpider",
            piles,
            action: 3
        ));
        var before = Count(
            f: f,
            game: "solitaireSpider",
            pile: 0
        );

        Settle(f: f);
        Assert.Equal(
            (allOccupied
            ? 1
            : -1),
            Value(
                f: f,
                game: "solitaireSpider",
                key: "result"
            )
        );
        Assert.Equal(
            (before - (allOccupied
            ? 10
            : 0)),
            Count(
                f: f,
                game: "solitaireSpider",
                pile: 0
            )
        );
    }
    [InlineData(1)]
    [InlineData(3)]
    [Theory]
    public void StockPassesPreserveOrderAndTheFinalShortDraw(int amount) {
        var definition = Position(
            "solitaireKlondike",
            new() { [0] = [0, 1, 2, 3, 4] },
            action: 0
        );

        definition = definition with { StateRaw = definition.StateRaw! with { World = [.. definition.State.Select(selector: r => ((r.Name.Value == "solitaireKlondike")
            ? r with { Cells = [.. r.Cells!.Select(selector: c => ((c.Key.Value == "activeOption")
                ? c with { Value = amount }
                : c))] }
            : r))] } };
        using var f = Fixtures.FreshServer(definition);

        Settle(f: f);
        Request(
            f,
            "solitaireKlondike",
            3
        );
        Assert.Equal(
            amount,
            Count(
                f: f,
                game: "solitaireKlondike",
                pile: 1
            )
        );
        while (Count(
            f: f,
            game: "solitaireKlondike",
            pile: 0
        ) > 0) { Request(
            f,
            "solitaireKlondike",
            3
        ); }
        Assert.Equal(
            new[] { "0", "1", "2", "3", "4" },
            Row(
                f: f,
                name: "solitaireKlondikePile1"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        Request(
            f,
            "solitaireKlondike",
            3
        );
        Assert.Equal(
            new[] { "0", "1", "2", "3", "4" },
            Row(
                f: f,
                name: "solitaireKlondikePile0"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        Assert.Equal(
            1,
            Value(
                f: f,
                game: "solitaireKlondike",
                key: "passes"
            )
        );
    }
    [Fact]
    public void SwitchingAwayPausesADealAndChangingAnOptionDoesNotChangeAnActiveDeal() {
        using var f = Fixtures.FreshServer(Game(
            "solitaireKlondike",
            source => {
            Set(
                game: "solitaireKlondike",
                key: "option",
                source: source,
                value: 3
            );
            Set(
                game: "solitaireKlondike",
                key: "action",
                source: source,
                value: 1
            );
            Set(
                game: "solitaireKlondike",
                key: "request",
                source: source,
                value: 1
            );
            Set(
                game: "solitaire",
                key: "table",
                source: source,
                value: 0
            );
        }
        ));

        Steps(
            f: f,
            n: 10
        );
        Assert.Equal(
            0,
            Value(
                f: f,
                game: "solitaireKlondike",
                key: "applied"
            )
        );
        f.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "solitaire",
            Value: 1,
            Key: "table",
            Kind: WorldDocumentWriteKind.Set
        ));
        Settle(
            f: f,
            game: "solitaireKlondike"
        );
        f.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "solitaireKlondike",
            Value: 1,
            Key: "option",
            Kind: WorldDocumentWriteKind.Set
        ));
        Request(
            f,
            "solitaireKlondike",
            3
        );
        Assert.Equal(
            3,
            Value(
                f: f,
                game: "solitaireKlondike",
                key: "activeOption"
            )
        );
        Assert.Equal(
            3,
            Count(
                f: f,
                game: "solitaireKlondike",
                pile: 1
            )
        );
    }
    [InlineData("solitaireKlondike")]
    [InlineData("solitaireFreecell")]
    [Theory]
    public void TheLastFoundationMoveWinsAndFurtherMovesAreRefused(string game) {
        var piles = new Dictionary<int, int[]> { [2] = [12], [10] = Enumerable.Range(
            count: 12,
            start: 0
        ).ToArray(), [11] = Enumerable.Range(
            count: 13,
            start: 13
        ).ToArray(), [12] = Enumerable.Range(
            count: 13,
            start: 26
        ).ToArray(), [13] = Enumerable.Range(
            count: 13,
            start: 39
        ).ToArray() };
        using var f = Fixtures.FreshServer(Position(
            game,
            piles,
            card: 12,
            to: 10
        ));

        Settle(
            f: f,
            game: game
        );
        Assert.Equal(
            2,
            Value(
                f: f,
                game: game,
                key: "status"
            )
        );
        Assert.Equal(
            1,
            Value(
                f: f,
                game: game,
                key: "moves"
            )
        );
        Request(
            action: 2,
            card: 12,
            f: f,
            from: 10,
            game: game,
            to: 2
        );
        Assert.Equal(
            -1,
            Value(
                f: f,
                game: game,
                key: "result"
            )
        );
        Assert.Equal(
            13,
            Count(
                f: f,
                game: game,
                pile: 10
            )
        );
    }
    [InlineData("solitaireKlondike", 2)]
    [InlineData("solitaireSpider", 3)]
    [InlineData("solitaireFreecell", 0)]
    [Theory]
    public void UnsupportedNewDealOptionsRefuseWithoutConsumingRandomness(string game, int option) {
        using var f = Fixtures.FreshServer(Game(
            game,
            source => Set(
                game: game,
                key: "option",
                source: source,
                value: option
            )
        ));

        Request(
            f,
            game,
            1
        );
        Assert.Equal(
            -1,
            Value(
                f: f,
                game: game,
                key: "result"
            )
        );
        Assert.Equal(
            0,
            Row(
                f: f,
                name: (game + "Stream")
            ).DrawCursor
        );
        Assert.Equal(
            ((game == "solitaireSpider")
            ? 104
            : 52),
            Count(
                f: f,
                game: game,
                pile: 0
            )
        );
    }

}
