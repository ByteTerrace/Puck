using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

public sealed class HeartsLawTests {
    private static readonly Lazy<WorldDefinition> Source = new(() => AuthoredGameFixtures.Load(
        "src/Puck.World/Assets/worlds/games/hearts.world.json"));
    private static WorldDefinition Game => Source.Value;

    private static WorldDefinition Configure(WorldDefinition game, params (string Row, string Key, long Value)[] values) => game with {
        StateRaw = game.StateRaw! with { World = game.StateRaw.World!.Select(row => row with {
            Cells = row.Cells?.Select(cell => values.FirstOrDefault(v => v.Row == row.Name.Value && v.Key == cell.Key.Value) is var replacement && replacement.Row is not null
                ? cell with { Value = CellValue.Int(replacement.Value) } : cell).ToArray(),
        }).ToArray() },
    };

    private static long Read(WorldDefinition game, string row, string key) => WorldDefinitionRows.FindStateRow(game.State, row)!.Cells!.Single(c => c.Key.Value == key).Value.Raw;
    private static WorldDefinition RulesOnly(params string[] names) => Game with { Rules = Game.Rules!.Where(r => names.Contains(r.Name.Value)).ToArray() };

    // Independent rules oracle, deliberately written as ordinary branches.
    private static bool Legal(int card, int actor, ulong hand, int turn, int led, int count, int trick, bool broken) {
        if (actor != turn || (hand & (1UL << card)) == 0) { return false; }
        var suit = card / 13;
        if (count == 0) {
            if (trick == 0 && card != 0) { return false; }
            if (suit == 2 && !broken && Enumerable.Range(0, 52).Any(i => (hand & (1UL << i)) != 0 && i / 13 != 2)) { return false; }
        } else if (suit != led && Enumerable.Range(0, 52).Any(i => i / 13 == led && (hand & (1UL << i)) != 0)) { return false; }
        if (trick == 0 && (suit == 2 || card == 49) && Enumerable.Range(0, 52).Any(i => i / 13 != 2 && i != 49 && (hand & (1UL << i)) != 0)) { return false; }
        return true;
    }

    [Fact]
    public void EveryCardMatchesIndependentLegalityAcrossOpeningAndMidgameHands() {
        var game = RulesOnly("hearts-play", "hearts-play-refuse");
        var fixture = new RuleArenaFixture(game);
        var random = new Random(195103);
        for (var sample = 0; sample < 100; sample++) {
            var cards = sample switch {
                0 => Enumerable.Range(26, 13).ToArray(),
                1 => Enumerable.Range(26, 12).Append(49).ToArray(),
                _ => Enumerable.Range(0, 52).OrderBy(_ => random.Next()).Take(13).ToArray(),
            };
            var mask = cards.Aggregate(0UL, (bits, card) => bits | (1UL << card));
            var actor = sample % 4 + 1;
            var turn = sample % 7 == 0 ? actor % 4 + 1 : actor;
            var count = sample % 4;
            var trick = sample % 3 == 0 ? 0 : 5;
            var led = sample % 4;
            var broken = sample % 2 == 0;
            for (var card = 0; card < 52; card++) {
                var position = Configure(game,
                    ("heartsTable", "stage", 3), ("heartsTable", "turn", turn), ("heartsTable", "inTrick", count),
                    ("heartsTable", "trickNo", trick), ("heartsTable", "ledSuit", led), ("heartsTable", "broken", broken ? 1 : 0),
                    ("heartsAct", "seat", actor), ("heartsAct", "safeSeat", actor), ("heartsAct", "seatOk", 1),
                    ("heartsAct", "card", card), ("heartsAct", "safeCard", card), ("heartsAct", "cardSuit", card / 13),
                    ("heartsAct", "cardRank", card % 13 + 2), ("heartsAct", "cardPoints", card == 49 ? 13 : card / 13 == 2 ? 1 : 0),
                    ("heartsAct", "mask", (long)mask), ("heartsAct", "inHand", (long)((mask >> card) & 1)),
                    ("heartsAct", "ledMask", 8191L << (led * 13)), ("heartsAct", "ready", 1), ("heartsAct", "request", 1));
                position = position with { StateRaw = position.StateRaw! with { World = position.StateRaw.World!.Select(row => row.Name.Value == $"heartsHand{actor}"
                    ? row with { Cells = cards.Select(i => new StateCell(CellName.Parse(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), CellValue.Bool(true))).ToArray() } : row).ToArray() } };
                fixture.Evaluate(position);
                Assert.Equal(Legal(card, actor, mask, turn, led, count, trick, broken) ? 1 : -1, fixture.Read("heartsAct", "result"));
            }
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void HandScoringAndMoonApplyOnce(int shooter) {
        var game = RulesOnly("hearts-hand-score", "hearts-match-finish");
        var position = Configure(game, ("heartsTable", "stage", 3), ("heartsTable", "trickNo", 13));
        for (var seat = 1; seat <= 4; seat++) { position = Configure(position, ("heartsRound", seat.ToString(), shooter == 0 ? (seat == 1 ? 13 : seat == 2 ? 7 : 3) : seat == shooter ? 26 : 0)); }
        var fixture = new RuleArenaFixture(position);
        fixture.Judge();
        fixture.Judge();
        Assert.Equal(4, fixture.Read("heartsTable", "stage"));
        for (var seat = 1; seat <= 4; seat++) { Assert.Equal(shooter == 0 ? (seat == 1 ? 13 : seat == 2 ? 7 : 3) : seat == shooter ? 0 : 26, fixture.Read("heartsScore", seat.ToString())); }
    }

    [Fact]
    public void HundredPointMatchLatchesTiedLowestSeats() {
        var game = Configure(RulesOnly("hearts-match-finish"), ("heartsTable", "stage", 4),
            ("heartsScore", "1", 100), ("heartsScore", "2", 20), ("heartsScore", "3", 20), ("heartsScore", "4", 80));
        var fixture = new RuleArenaFixture(game);
        fixture.Judge();
        Assert.Equal(1, fixture.Read("heartsTable", "matchOver"));
        Assert.Equal(6, fixture.Read("heartsTable", "winnerMask"));
    }

    [Fact]
    public void FiveHandsConserveTheDeckAndRotatePassingThroughHold() {
        var names = new[] { "hearts-layout", "hearts-place-", "hearts-physical", "hearts-observe", "hearts-ai-", "hearts-rest" };
        var game = Configure(Game with { Rules = Game.Rules!.Where(r => !names.Any(prefix => r.Name.Value.StartsWith(prefix, StringComparison.Ordinal))).ToArray() }, ("heartsOptions", "target", 10000));
        var fixture = new RuleArenaFixture(game);
        void Advance() { for (var tick = 0; tick < 8; tick++) { fixture.Judge(); } }
        void Act(int seat, int card) {
            fixture.Write("heartsAct", "seat", seat);
            fixture.Write("heartsAct", "card", card);
            fixture.Write("heartsAct", "request", fixture.Read("heartsAct", "request") + 1);
            Advance();
            Assert.Equal(1, fixture.Read("heartsAct", "result"));
        }
        for (var hand = 1; hand <= 5; hand++) {
            Advance();
            Assert.Equal(hand, fixture.Read("heartsTable", "handNo"));
            var offset = new[] { 1, 3, 2, 0 }[(hand - 1) % 4];
            Assert.Equal(offset, fixture.Read("heartsTable", "passDir"));
            var original = Enumerable.Range(1, 4).Select(seat => (ulong)fixture.Read("heartsHandMask", seat.ToString())).ToArray();
            Assert.Equal((1UL << 52) - 1, original.Aggregate(0UL, (a, b) => a | b));
            if (offset != 0) {
                var passed = new ulong[4];
                for (var seat = 1; seat <= 4; seat++) {
                    for (var pick = 0; pick < 3; pick++) {
                        var card = Enumerable.Range(0, 52).First(i => ((ulong)fixture.Read("heartsHandMask", seat.ToString()) & (1UL << i)) != 0);
                        passed[seat - 1] |= 1UL << card;
                        Act(seat, card);
                    }
                }
                for (var seat = 0; seat < 4; seat++) {
                    Assert.Equal((original[seat] & ~passed[seat]) | passed[(seat - offset + 4) % 4], (ulong)fixture.Read("heartsHandMask", (seat + 1).ToString()));
                }
            }
            Assert.Equal(3, fixture.Read("heartsTable", "stage"));
            var trickCards = new List<(int Seat, int Card)>();
            var points = new long[4];
            for (var play = 0; play < 52; play++) {
                var turn = (int)fixture.Read("heartsTable", "turn");
                var mask = (ulong)fixture.Read("heartsHandMask", turn.ToString());
                var card = Enumerable.Range(0, 52).First(i => Legal(i, turn, mask, turn,
                    (int)fixture.Read("heartsTable", "ledSuit"), (int)fixture.Read("heartsTable", "inTrick"),
                    (int)fixture.Read("heartsTable", "trickNo"), fixture.Read("heartsTable", "broken") == 1));
                Act(turn, card);
                trickCards.Add((turn, card));
                if (trickCards.Count == 4) {
                    var suit = trickCards[0].Card / 13;
                    var winner = trickCards.Where(c => c.Card / 13 == suit).MaxBy(c => c.Card % 13).Seat;
                    points[winner - 1] += trickCards.Sum(c => c.Card == 49 ? 13 : c.Card / 13 == 2 ? 1 : 0);
                    Assert.Equal(winner, fixture.Read("heartsTable", "turn"));
                    for (var seat = 1; seat <= 4; seat++) { Assert.Equal(points[seat - 1], fixture.Read("heartsRound", seat.ToString())); }
                    trickCards.Clear();
                }
            }
            Assert.Equal(4, fixture.Read("heartsTable", "stage"));
            Assert.Equal(26, Enumerable.Range(1, 4).Sum(seat => fixture.Read("heartsRound", seat.ToString())));
            Assert.Equal(0, fixture.Read("heartsTable", "refused"));
            fixture.Write("heartsTable", "deal", 1);
        }
    }

    [Fact]
    public void PassingAiDoesNotReadOpponentsHands() {
        var game = Configure(RulesOnly("hearts-ai-reset-1", "hearts-ai-select-1", "hearts-ai-commit-1"),
            ("heartsTable", "stage", 1), ("heartsTable", "passDir", 1), ("heartsPhysical", "valid", 1),
            ("heartsAi", "clock", 1), ("heartsOptions", "pace", 1), ("heartsOptions", "aiMask", 1),
            ("heartsHandMask", "1", (1L << 49) | 4095));
        game = game with { StateRaw = game.StateRaw! with { World = game.StateRaw.World!.Select(row => row.Name.Value == "heartsHand1"
            ? row with { Cells = Enumerable.Range(0, 12).Append(49).Select(i => new StateCell(CellName.Parse(i.ToString()), CellValue.Bool(true))).ToArray() } : row).ToArray() } };
        var fixture = new RuleArenaFixture(game);
        fixture.Evaluate(game);
        var choice = fixture.Read("heartsAi", "card");
        Assert.Equal(49, choice);
        for (var sample = 0; sample < 52; sample++) {
            fixture.Evaluate(Configure(game, ("heartsHandMask", "2", 1L << sample),
                ("heartsHandMask", "3", ((1L << 52) - 1) ^ (1L << sample)), ("heartsHandMask", "4", 0)));
            Assert.Equal(choice, fixture.Read("heartsAi", "card"));
        }
    }

    [Fact]
    public void ChangedTurnOrDisabledSeatDiscardsPendingAiChoice() {
        foreach (var enabled in new[] { 0, 1 }) {
            var game = Configure(RulesOnly("hearts-ai-clock"), ("heartsTable", "stage", 3),
                ("heartsTable", "turn", enabled == 1 ? 2 : 1), ("heartsOptions", "aiMask", enabled),
                ("heartsAi", "card", 0), ("heartsAi", "seat", 1), ("heartsAi", "stamp", 0),
                ("heartsPhysical", "valid", 1));
            var fixture = new RuleArenaFixture(game);
            fixture.Judge();
            Assert.Equal(-1, fixture.Read("heartsAi", "card"));
        }
    }

    [Fact]
    public void PhysicalPassRejectsWrongAreaAndAcceptsCorrection() {
        using var fixture = Fixtures.FreshServer(Configure(Game, ("heartsOptions", "aiMask", 0)));
        void Step(int count) { for (var tick = 0; tick < count; tick++) { fixture.Step(); } }
        Step(500);
        Assert.Equal(1, Read(fixture.Server.Definition, "heartsPhysical", "valid"));
        var card = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "heartsHand1")!.Cells![0].Key.Value;
        var ordinal = fixture.Server.Definition.Placements.Select((placement, index) => (placement, index)).Single(p => p.placement.Id == $"card{card}").index;
        var body = fixture.Server.Body(fixture.Server.Population.BodyForPlacementOrdinal(ordinal))!;
        var topology = WorldTopologyCompilation.Find(fixture.Server.Definition, "heartsPlaces")!;
        void Place(int cell) => body.Pose(position: topology.CellCentre(cell), yawRadians: FixedQ4816.Zero,
            pitchRadians: FixedQ4816.Zero, rollRadians: FixedQ4816.Zero);
        Place(64);
        Step(150);
        Assert.Equal(0, Read(fixture.Server.Definition, "heartsTable", "accepted"));
        Assert.Equal(0, Read(fixture.Server.Definition, "heartsPhysical", "valid"));
        Place(52);
        Step(200);
        Assert.Equal(1, Read(fixture.Server.Definition, "heartsTable", "accepted"));
        Assert.Equal(1, Read(fixture.Server.Definition, "heartsPhysical", "valid"));
        Assert.Equal(card, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "heartsPass1")!.Cells![0].Key.Value);
        Assert.Empty(fixture.Server.RuleRuntimeDiagnostics());
    }

    [Fact]
    public void AiCompletesAFullPhysicalHandWithoutRefusedMoves() {
        using var fixture = Fixtures.FreshServer(Configure(Game, ("heartsOptions", "aiMask", 15), ("heartsOptions", "pace", 1)));
        for (var tick = 0; tick < 12000 && Read(fixture.Server.Definition, "heartsTable", "stage") != 4; tick++) { fixture.Step(); }
        var game = fixture.Server.Definition;
        Assert.True(Read(game, "heartsTable", "stage") == 4, $"stage={Read(game, "heartsTable", "stage")}, accepted={Read(game, "heartsTable", "accepted")}, refused={Read(game, "heartsTable", "refused")}, changes={Read(game, "heartsPhysical", "changed")}, settle={Read(game, "heartsPhysical", "settle")}, ai={Read(game, "heartsAi", "card")}");
        Assert.Equal(64, Read(game, "heartsTable", "accepted"));
        Assert.Equal(0, Read(game, "heartsTable", "refused"));
        Assert.Equal(26, Enumerable.Range(1, 4).Sum(seat => Read(game, "heartsRound", seat.ToString())));
        Assert.Equal(52, Enumerable.Range(1, 4).Sum(seat => WorldDefinitionRows.FindStateRow(game.State, $"heartsWon{seat}")!.Cells?.Count ?? 0));
        Assert.Empty(fixture.Server.RuleRuntimeDiagnostics());
    }
}
