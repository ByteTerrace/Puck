using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Gameplay laws compiled directly from the Parlor package's canonical Hearts source.</summary>
public sealed class HeartsLawTests {
    private static readonly Lazy<WorldDefinition> Source = new(valueFactory: () => AuthoredGameFixtures.Load(
        "worlds/parlor/hearts.puck"));

    private static WorldDefinition Game => Source.Value;

    private static WorldDefinition Configure(WorldDefinition game, params (string Row, string Key, long Value)[] values) => game with {
        StateRaw = game.StateRaw! with {
            World = game.StateRaw.World!.Select(selector: row => (!values.Any(predicate: v => (v.Row == row.Name.Value)) ? row : row with {
                Cells = row.Cells?.Select(selector: cell => (((values.FirstOrDefault(predicate: v => ((v.Row == row.Name.Value) && (v.Key == cell.Key.Value))) is var replacement) && (replacement.Row is not null))
                    ? cell with { Value = CellValue.Int(value: replacement.Value) } : cell)).ToArray(),
            })).ToArray(),
        },
    };
    private static long Read(WorldDefinition game, string row, string key) => WorldDefinitionRows.FindStateRow(game.State, row)!.Cells!.Single(predicate: c => (c.Key.Value == key)).Value.Raw;
    private static WorldDefinition RulesOnly(params string[] names) => Game with { Rules = Game.Rules!.Where(predicate: r => names.Contains(value: r.Name.Value)).ToArray() };
    // Independent rules oracle, deliberately written as ordinary branches.
    private static bool Legal(int card, int actor, ulong hand, int turn, int led, int count, int trick, bool broken) {
        if ((actor != turn) || ((hand & (1UL << card)) == 0)) { return false; }
        var suit = (card / 13);

        if (count == 0) {
            if ((trick == 0) && (card != 0)) { return false; }
            if ((suit == 2) && !broken && Enumerable.Range(count: 52, start: 0).Any(predicate: i => (((hand & (1UL << i)) != 0) && ((i / 13) != 2)))) { return false; }
        } else if ((suit != led) && Enumerable.Range(count: 52, start: 0).Any(predicate: i => (((i / 13) == led) && ((hand & (1UL << i)) != 0)))) { return false; }
        if ((trick == 0) && ((suit == 2) || (card == 49)) && Enumerable.Range(count: 52, start: 0).Any(predicate: i => (((i / 13) != 2) && (i != 49) && ((hand & (1UL << i)) != 0)))) { return false; }
        return true;
    }

    [Fact]
    public void EveryCardMatchesIndependentLegalityAcrossOpeningAndMidgameHands() {
        var game = RulesOnly("hearts-play", "hearts-play-refuse");
        var fixture = new RuleArenaFixture(definition: game);
        var random = new Random(Seed: 195103);

        for (var sample = 0; (sample < 100); sample++) {
            var cards = sample switch {
                0 => Enumerable.Range(count: 13, start: 26).ToArray(),
                1 => Enumerable.Range(count: 12, start: 26).Append(element: 49).ToArray(),
                _ => Enumerable.Range(count: 52, start: 0).OrderBy(keySelector: _ => random.Next()).Take(count: 13).ToArray(),
            };
            var mask = cards.Aggregate(func: (bits, card) => bits | (1UL << card), seed: 0UL);
            var actor = ((sample % 4) + 1);
            var turn = (((sample % 7) == 0) ? ((actor % 4) + 1) : actor);
            var count = (sample % 4);
            var trick = (((sample % 3) == 0) ? 0 : 5);
            var led = (sample % 4);
            var broken = ((sample % 2) == 0);
            // The table and the hand are the sample's; only the offered card varies, so each card replaces the one
            // heartsAct row and every other row stays the instance the sample's first card loaded.
            var dealt = Configure(game,
                ("heartsTable", "stage", 3), ("heartsTable", "turn", turn), ("heartsTable", "inTrick", count),
                ("heartsTable", "trickNo", trick), ("heartsTable", "ledSuit", led), ("heartsTable", "broken", (broken ? 1 : 0)));

            dealt = dealt with {
                StateRaw = dealt.StateRaw! with {
                    World = dealt.StateRaw.World!.Select(selector: row => ((row.Name.Value == $"heartsHand{actor}")
                ? row with { Cells = cards.Select(selector: i => new StateCell(CellName.Parse(candidate: i.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)), CellValue.Bool(value: true))).ToArray() } : row)).ToArray(),
                },
            };
            for (var card = 0; (card < 52); card++) {
                var position = Configure(dealt,
                    ("heartsAct", "seat", actor), ("heartsAct", "safeSeat", actor), ("heartsAct", "seatOk", 1),
                    ("heartsAct", "card", card), ("heartsAct", "safeCard", card), ("heartsAct", "cardSuit", (card / 13)),
                    ("heartsAct", "cardRank", ((card % 13) + 2)), ("heartsAct", "cardPoints", ((card == 49) ? 13 : (((card / 13) == 2) ? 1 : 0))),
                    ("heartsAct", "mask", ((long)mask)), ("heartsAct", "inHand", ((long)((mask >> card) & 1))),
                    ("heartsAct", "ledMask", (8191L << (led * 13))), ("heartsAct", "ready", 1), ("heartsAct", "request", 1));

                fixture.Evaluate(position: position);
                Assert.Equal((Legal(actor: actor, broken: broken, card: card, count: count, hand: mask, led: led, trick: trick, turn: turn) ? 1 : -1), fixture.Read(key: "result", row: "heartsAct"));
            }
        }
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [Theory]
    public void HandScoringAndMoonApplyOnce(int shooter) {
        var game = RulesOnly("hearts-hand-score", "hearts-match-finish");
        var position = Configure(game, ("heartsTable", "stage", 3), ("heartsTable", "trickNo", 13));

        for (var seat = 1; (seat <= 4); seat++) { position = Configure(position, ("heartsRound", seat.ToString(), ((shooter == 0) ? ((seat == 1) ? 13 : ((seat == 2) ? 7 : 3)) : ((seat == shooter) ? 26 : 0)))); }
        var fixture = new RuleArenaFixture(definition: position);

        fixture.Judge();
        fixture.Judge();
        Assert.Equal(4, fixture.Read(key: "stage", row: "heartsTable"));
        for (var seat = 1; (seat <= 4); seat++) { Assert.Equal(((shooter == 0) ? ((seat == 1) ? 13 : ((seat == 2) ? 7 : 3)) : ((seat == shooter) ? 0 : 26)), fixture.Read("heartsScore", seat.ToString())); }
    }
    [Fact]
    public void HundredPointMatchLatchesTiedLowestSeats() {
        var game = Configure(RulesOnly("hearts-match-finish"), ("heartsTable", "stage", 4),
            ("heartsScore", "1", 100), ("heartsScore", "2", 20), ("heartsScore", "3", 20), ("heartsScore", "4", 80));
        var fixture = new RuleArenaFixture(definition: game);

        fixture.Judge();
        Assert.Equal(1, fixture.Read(key: "matchOver", row: "heartsTable"));
        Assert.Equal(6, fixture.Read(key: "winnerMask", row: "heartsTable"));
    }
    [Fact]
    public void FiveHandsConserveTheDeckAndRotatePassingThroughHold() {
        var names = new[] { "hearts-layout", "hearts-place-", "hearts-physical", "hearts-observe", "hearts-ai-", "hearts-rest" };
        var game = Configure(Game with { Rules = Game.Rules!.Where(predicate: r => !names.Any(predicate: prefix => r.Name.Value.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix))).ToArray() }, ("heartsOptions", "target", 10000));
        var fixture = new RuleArenaFixture(definition: game);

        void Advance() { for (var tick = 0; (tick < 8); tick++) { fixture.Judge(); } }
        void Act(int seat, int card) {
            fixture.Write(key: "seat", row: "heartsAct", value: seat);
            fixture.Write(key: "card", row: "heartsAct", value: card);
            fixture.Write("heartsAct", "request", (fixture.Read(key: "request", row: "heartsAct") + 1));
            Advance();
            Assert.Equal(1, fixture.Read(key: "result", row: "heartsAct"));
        }
        for (var hand = 1; (hand <= 5); hand++) {
            Advance();
            Assert.Equal(hand, fixture.Read(key: "handNo", row: "heartsTable"));
            var offset = new[] { 1, 3, 2, 0 }[((hand - 1) % 4)];

            Assert.Equal(offset, fixture.Read(key: "passDir", row: "heartsTable"));
            var original = Enumerable.Range(count: 4, start: 1).Select(selector: seat => ((ulong)fixture.Read("heartsHandMask", seat.ToString()))).ToArray();

            Assert.Equal(((1UL << 52) - 1), original.Aggregate(func: (a, b) => a | b, seed: 0UL));
            if (offset != 0) {
                var passed = new ulong[4];

                for (var seat = 1; (seat <= 4); seat++) {
                    for (var pick = 0; (pick < 3); pick++) {
                        var card = Enumerable.Range(count: 52, start: 0).First(predicate: i => ((((ulong)fixture.Read("heartsHandMask", seat.ToString())) & (1UL << i)) != 0));

                        passed[(seat - 1)] |= (1UL << card);
                        Act(card: card, seat: seat);
                    }
                }
                for (var seat = 0; (seat < 4); seat++) {
                    Assert.Equal((original[seat] & ~passed[seat]) | passed[(((seat - offset) + 4) % 4)], ((ulong)fixture.Read("heartsHandMask", (seat + 1).ToString())));
                }
            }
            Assert.Equal(3, fixture.Read(key: "stage", row: "heartsTable"));
            var trickCards = new List<(int Seat, int Card)>();
            var points = new long[4];

            for (var play = 0; (play < 52); play++) {
                var turn = ((int)fixture.Read(key: "turn", row: "heartsTable"));
                var mask = ((ulong)fixture.Read("heartsHandMask", turn.ToString()));
                var card = Enumerable.Range(count: 52, start: 0).First(predicate: i => Legal(i, turn, mask, turn,
                    ((int)fixture.Read(key: "ledSuit", row: "heartsTable")), ((int)fixture.Read(key: "inTrick", row: "heartsTable")),
                    ((int)fixture.Read(key: "trickNo", row: "heartsTable")), (fixture.Read(key: "broken", row: "heartsTable") == 1)));

                Act(card: card, seat: turn);
                trickCards.Add(item: (turn, card));
                if (trickCards.Count == 4) {
                    var suit = (trickCards[0].Card / 13);
                    var winner = trickCards.Where(predicate: c => ((c.Card / 13) == suit)).MaxBy(keySelector: c => (c.Card % 13)).Seat;

                    points[(winner - 1)] += trickCards.Sum(selector: c => ((c.Card == 49) ? 13 : (((c.Card / 13) == 2) ? 1 : 0)));
                    Assert.Equal(winner, fixture.Read(key: "turn", row: "heartsTable"));
                    for (var seat = 1; (seat <= 4); seat++) { Assert.Equal(points[(seat - 1)], fixture.Read("heartsRound", seat.ToString())); }
                    trickCards.Clear();
                }
            }
            Assert.Equal(4, fixture.Read(key: "stage", row: "heartsTable"));
            Assert.Equal(26, Enumerable.Range(count: 4, start: 1).Sum(selector: seat => fixture.Read("heartsRound", seat.ToString())));
            Assert.Equal(0, fixture.Read(key: "refused", row: "heartsTable"));
            fixture.Write(key: "deal", row: "heartsTable", value: 1);
        }
    }
    [Fact]
    public void PassingAiDoesNotReadOpponentsHands() {
        var game = Configure(RulesOnly("hearts-ai-reset-1", "hearts-ai-select-1", "hearts-ai-commit-1"),
            ("heartsTable", "stage", 1), ("heartsTable", "passDir", 1), ("heartsPhysical", "valid", 1),
            ("heartsAi", "clock", 1), ("heartsOptions", "pace", 1), ("heartsOptions", "aiMask", 1),
            ("heartsHandMask", "1", (1L << 49) | 4095));

        game = game with {
            StateRaw = game.StateRaw! with {
                World = game.StateRaw.World!.Select(selector: row => ((row.Name.Value == "heartsHand1")
            ? row with { Cells = Enumerable.Range(count: 12, start: 0).Append(element: 49).Select(selector: i => new StateCell(CellName.Parse(candidate: i.ToString()), CellValue.Bool(value: true))).ToArray() } : row)).ToArray(),
            },
        };
        var fixture = new RuleArenaFixture(definition: game);

        fixture.Evaluate(position: game);
        var choice = fixture.Read(key: "card", row: "heartsAi");

        Assert.Equal(actual: choice, expected: 49);
        for (var sample = 0; (sample < 52); sample++) {
            fixture.Evaluate(position: Configure(game, ("heartsHandMask", "2", (1L << sample)),
                ("heartsHandMask", "3", ((1L << 52) - 1) ^ (1L << sample)), ("heartsHandMask", "4", 0)));
            Assert.Equal(choice, fixture.Read(key: "card", row: "heartsAi"));
        }
    }
    [Fact]
    public void ChangedTurnOrDisabledSeatDiscardsPendingAiChoice() {
        foreach (var enabled in new[] { 0, 1 }) {
            var game = Configure(RulesOnly("hearts-ai-clock"), ("heartsTable", "stage", 3),
                ("heartsTable", "turn", ((enabled == 1) ? 2 : 1)), ("heartsOptions", "aiMask", enabled),
                ("heartsAi", "card", 0), ("heartsAi", "seat", 1), ("heartsAi", "stamp", 0),
                ("heartsPhysical", "valid", 1));
            var fixture = new RuleArenaFixture(definition: game);

            fixture.Judge();
            Assert.Equal(-1, fixture.Read(key: "card", row: "heartsAi"));
        }
    }
    [Fact]
    public void PhysicalPassRejectsWrongAreaAndAcceptsCorrection() {
        using var fixture = Fixtures.FreshServer(Configure(Game, ("heartsOptions", "aiMask", 0)));

        void Step(int count) { for (var tick = 0; (tick < count); tick++) { fixture.Step(); } }
        Step(count: 500);
        Assert.Equal(1, Read(fixture.Server.Definition, "heartsPhysical", "valid"));
        var card = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "heartsHand1")!.Cells![0].Key.Value;
        var ordinal = fixture.Server.Definition.Placements.Select(selector: (placement, index) => (placement, index)).Single(predicate: p => (p.placement.Id == $"card{card}")).index;
        var body = fixture.Server.Body(index: fixture.Server.Population.BodyForPlacementOrdinal(ordinal: ordinal))!;
        var topology = WorldTopologyCompilation.Find(definition: fixture.Server.Definition, name: "heartsPlaces")!;

        void Place(int cell) => body.Pose(position: topology.CellCentre(cell: cell), yawRadians: FixedQ4816.Zero,
            pitchRadians: FixedQ4816.Zero, rollRadians: FixedQ4816.Zero);
        Place(cell: 64);
        Step(count: 150);
        Assert.Equal(0, Read(fixture.Server.Definition, "heartsTable", "accepted"));
        Assert.Equal(0, Read(fixture.Server.Definition, "heartsPhysical", "valid"));
        Place(cell: 52);
        Step(count: 200);
        Assert.Equal(1, Read(fixture.Server.Definition, "heartsTable", "accepted"));
        Assert.Equal(1, Read(fixture.Server.Definition, "heartsPhysical", "valid"));
        Assert.Equal(card, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "heartsPass1")!.Cells![0].Key.Value);
        Assert.Empty(collection: fixture.Server.RuleRuntimeDiagnostics());
    }
    [Fact]
    public void AiCompletesAFullPhysicalHandWithoutRefusedMoves() {
        using var fixture = Fixtures.FreshServer(Configure(Game, ("heartsOptions", "aiMask", 15), ("heartsOptions", "pace", 1)));

        for (var tick = 0; ((tick < 12000) && (Read(fixture.Server.Definition, "heartsTable", "stage") != 4)); tick++) { fixture.Step(); }
        var game = fixture.Server.Definition;

        Assert.True(condition: (Read(game: game, key: "stage", row: "heartsTable") == 4), userMessage: $"stage={Read(game: game, key: "stage", row: "heartsTable")}, accepted={Read(game: game, key: "accepted", row: "heartsTable")}, refused={Read(game: game, key: "refused", row: "heartsTable")}, changes={Read(game: game, key: "changed", row: "heartsPhysical")}, settle={Read(game: game, key: "settle", row: "heartsPhysical")}, ai={Read(game: game, key: "card", row: "heartsAi")}");
        Assert.Equal(64, Read(game: game, key: "accepted", row: "heartsTable"));
        Assert.Equal(0, Read(game: game, key: "refused", row: "heartsTable"));
        Assert.Equal(26, Enumerable.Range(count: 4, start: 1).Sum(selector: seat => Read(game, "heartsRound", seat.ToString())));
        Assert.Equal(52, Enumerable.Range(count: 4, start: 1).Sum(selector: seat => (WorldDefinitionRows.FindStateRow(game.State, $"heartsWon{seat}")!.Cells?.Count ?? 0)));
        Assert.Empty(collection: fixture.Server.RuleRuntimeDiagnostics());
    }
}

