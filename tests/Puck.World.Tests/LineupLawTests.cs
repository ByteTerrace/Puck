using System.Globalization;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Gameplay laws compiled directly from the Parlor package's lineup source: the roster separates every
/// pair of busts, the rules eliminate exactly what a brute-force filter of the roster would, and the computer names
/// every possible bust within six turns.</summary>
/// <remarks>The oracle reads each bust's mask and applies the question's bit test directly; the rules eliminate
/// through the precomputed answer sets and one AND, so the two agree only if both are right.</remarks>
public sealed class LineupLawTests {
    private const long All = ((1L << Busts) - 1L);
    private const int Busts = 24;
    private const int Questions = 13;

    private static readonly Lazy<WorldDefinition> Source = new(valueFactory: () => AuthoredGameFixtures.Load("worlds/parlor/lineup.puck"));

    private static WorldDefinition Game => Source.Value;

    private static string Key(int value) => value.ToString(provider: CultureInfo.InvariantCulture);
    private static long Mask(int bust) => WorldDefinitionRows.FindStateRow(Game.State, "lineupPeople")!.Cells!.Single(predicate: cell => (cell.Key.Value == Key(value: bust))).Value.Raw;
    private static WorldDefinition RulesOnly(params string[] prefixes) => Game with {
        Rules = Game.Rules!.Where(predicate: rule => prefixes.Any(predicate: prefix => rule.Name.Value.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix))).ToArray(),
    };
    // Opens a game past its deal: both secrets written, both standing sets whole, seat `turn` to act.
    private static RuleArenaFixture Opened(WorldDefinition game, int secret1, int secret2, int turn, int cpuMask) {
        var fixture = new RuleArenaFixture(definition: game);

        fixture.Write(key: "stage", row: "lineupTable", value: 1);
        fixture.Write(key: "turn", row: "lineupTable", value: turn);
        fixture.Write(key: "cpuMask", row: "lineupOptions", value: cpuMask);
        fixture.Write(key: "pace", row: "lineupOptions", value: 1);
        fixture.Write(key: "$value", row: "lineupSecret1", value: secret1);
        fixture.Write(key: "$value", row: "lineupSecret2", value: secret2);
        fixture.Write(key: "$value", row: "lineupStanding1", value: All);
        fixture.Write(key: "$value", row: "lineupStanding2", value: All);

        return fixture;
    }
    private static void Act(RuleArenaFixture fixture, int seat, int kind, int value) {
        fixture.Write(key: "seat", row: "lineupAct", value: seat);
        fixture.Write(key: "kind", row: "lineupAct", value: kind);
        fixture.Write(key: "value", row: "lineupAct", value: value);
        fixture.Write("lineupAct", "request", (fixture.Read(key: "request", row: "lineupAct") + 1));
        fixture.Judge();
    }
    // What a seat's standing set should be after the answers it has heard: every bust whose mask agrees with the
    // secret on every question asked.
    private static long Oracle(int secret, IEnumerable<int> asked) {
        var keep = 0L;

        for (var bust = 0; (bust < Busts); bust++) {
            if (asked.All(predicate: question => (((Mask(bust: bust) >> question) & 1L) == ((Mask(bust: secret) >> question) & 1L)))) {
                keep |= (1L << bust);
            }
        }

        return keep;
    }

    [Fact]
    public void EveryPairOfBustsDiffersAndEachWearsOneHairAndOneEyeColour() {
        for (var bust = 0; (bust < Busts); bust++) {
            Assert.Equal(1L, long.PopCount(value: Mask(bust: bust) & 0b1111L));
            Assert.Equal(1L, long.PopCount(value: (Mask(bust: bust) >> 4) & 0b111L));
            Assert.Equal(0L, (Mask(bust: bust) >> Questions));
        }
        for (var first = 0; (first < Busts); first++) {
            for (var second = (first + 1); (second < Busts); second++) {
                Assert.True(condition: (Mask(bust: first) != Mask(bust: second)), userMessage: $"busts {first} and {second} carry the same mask {Mask(bust: first)}");
            }
        }
    }
    [Fact]
    public void EveryAnswerSetIsTheBustsCarryingItsQuestionsBit() {
        var yes = WorldDefinitionRows.FindStateRow(Game.State, "lineupYes")!;

        for (var question = 0; (question < Questions); question++) {
            var expected = Enumerable.Range(count: Busts, start: 0).Where(predicate: bust => (((Mask(bust: bust) >> question) & 1L) == 1L)).Aggregate(func: (set, bust) => set | (1L << bust), seed: 0L);

            Assert.Equal(expected, yes.Cells!.Single(predicate: cell => (cell.Key.Value == Key(value: question))).Value.Raw);
        }
    }
    // Both seats ask in turn, every question drawn at random, against random secrets: after every answer each
    // seat's standing set is the oracle's filter of the roster by the answers that seat heard.
    [Fact]
    public void EveryAnswerEliminatesExactlyWhatABruteForceFilterWould() {
        var game = RulesOnly("lineup-act");
        var random = new Random(Seed: 24013);

        for (var sample = 0; (sample < 60); sample++) {
            var secret1 = random.Next(maxValue: Busts);
            var secret2 = random.Next(maxValue: Busts);
            var fixture = Opened(game, secret1, secret2, turn: 1, cpuMask: 0);
            var heard = new List<int>[] { [], [] };

            for (var turn = 0; (turn < 8); turn++) {
                var seat = ((turn % 2) + 1);
                var question = random.Next(maxValue: Questions);

                Act(fixture, seat, kind: 1, value: question);
                heard[(seat - 1)].Add(item: question);

                Assert.Equal(1, fixture.Read(key: "result", row: "lineupAct"));
                Assert.Equal(Oracle(secret2, heard[0]), fixture.Read("lineupStanding1"));
                Assert.Equal(Oracle(secret1, heard[1]), fixture.Read("lineupStanding2"));
                Assert.Equal((Mask(bust: ((seat == 1) ? secret2 : secret1)) >> question) & 1L, fixture.Read(key: "lastAnswer", row: "lineupTable"));
            }
        }
    }
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(23, 2)]
    [Theory]
    public void NamingTheOtherSeatsBustWinsAndAnyOtherLoses(int named, int winner) {
        var fixture = Opened(RulesOnly("lineup-act"), secret1: 4, secret2: ((winner == 1) ? named : 0), turn: 1, cpuMask: 0);

        Act(fixture, seat: 1, kind: 2, value: named);

        Assert.Equal(2, fixture.Read(key: "stage", row: "lineupTable"));
        Assert.Equal(winner, fixture.Read(key: "winner", row: "lineupTable"));
    }
    // Against every bust seat 1 could hold, the computer on seat 2 names it within six of its turns, however seat 1
    // spends its own: here it wastes every turn on a question it has already asked.
    [Theory]
    [MemberData(nameof(EveryBust))]
    public void TheComputerNamesEveryBustWithinSixTurns(int secret) {
        var fixture = Opened(RulesOnly("lineup-act", "lineup-cpu-"), secret1: secret, secret2: 0, turn: 2, cpuMask: 2);

        for (var tick = 0; ((tick < 200) && (fixture.Read(key: "stage", row: "lineupTable") == 1)); tick++) {
            if (fixture.Read(key: "turn", row: "lineupTable") == 1) {
                Act(fixture, seat: 1, kind: 1, value: 0);
            } else {
                fixture.Judge();
            }
        }

        // At most five questions and then the name: six turns.
        Assert.Equal(2, fixture.Read(key: "stage", row: "lineupTable"));
        Assert.Equal(2, fixture.Read(key: "winner", row: "lineupTable"));
        Assert.Equal(secret, fixture.Read(key: "lastGuess", row: "lineupTable"));
        Assert.InRange(fixture.Read(key: "2", row: "lineupAsked"), 0L, 5L);
    }
    // Through a real server: every part a gallery deals for bust k stands where bust k's nameplate stands, and an
    // answer that rules a bust out lays its face down and deals its other parts nothing.
    [Fact]
    public void EveryDealtPartStandsOnItsBustsPlaceAndARuledOutBustLiesDown() {
        using var fixture = Fixtures.FreshServer(definition: Game);
        var step = Puck.Hosting.EngineTicks.PerRate(ratePerSecond: 60U);

        void Advance(int ticks) {
            for (var tick = 0; (tick < ticks); tick++) {
                fixture.Server.Advance(stepTicks: step);
            }
        }
        void Set(string row, string key, long value) => fixture.Server.EnqueueMutation(mutation: new Puck.World.Protocol.WorldMutation.UpsertStateCell(
            Principal: Puck.Commands.Principal.Console, Row: row, Key: key, Value: value, Kind: Puck.World.Protocol.WorldDocumentWriteKind.Set));
        WorldPlacement Placement(string id) => WorldDefinitionRows.FindPlacement(id: id, placements: fixture.Server.Definition.Placements)!;

        Advance(ticks: 10);

        foreach (var seat in new[] { 1, 2 }) {
            foreach (var layer in new[] { "Face", "Hair", "Eyes", "Glasses", "Hat", "Beard", "Earrings", "Nose" }) {
                for (var bust = 0; (bust < Busts); bust++) {
                    var part = WorldDefinitionRows.ResolvedFrame(definition: fixture.Server.Definition, placement: Placement(id: $"lineupGallery{seat}{layer}/{Key(value: bust)}")).Position;
                    var plate = WorldDefinitionRows.ResolvedFrame(definition: fixture.Server.Definition, placement: Placement(id: $"lineupPlate{seat}x{Key(value: bust)}")).Position;

                    Assert.True(condition: (System.Numerics.Vector3.Distance(value1: part, value2: plate) < 1e-4f), userMessage: $"seat {seat} {layer} part of bust {bust} stands at {part}, its plate at {plate}");
                }
            }
        }

        // Seat 1 asks question 9 about seat 2's bust; every bust whose answer differs is ruled out on seat 1's side.
        var secret2 = fixture.Server.Definition.State.Single(predicate: row => (row.Name.Value == "lineupSecret2")).Cells!.Single().Value.Raw;

        Set(key: "seat", row: "lineupAct", value: 1);
        Set(key: "kind", row: "lineupAct", value: 1);
        Set(key: "value", row: "lineupAct", value: 9);
        Set(key: "request", row: "lineupAct", value: 1);
        Advance(ticks: 10);

        for (var bust = 0; (bust < Busts); bust++) {
            var standing = (((Mask(bust: bust) >> 9) & 1L) == ((Mask(bust: ((int)secret2)) >> 9) & 1L));

            Assert.Equal((standing ? "lineupBustUp" : "lineupBustDown"), Placement(id: $"lineupGallery1Face/{Key(value: bust)}").PrototypeId);
            Assert.Equal("lineupBustUp", Placement(id: $"lineupGallery2Face/{Key(value: bust)}").PrototypeId);

            if (!standing) {
                Assert.Equal("lineupBlank", Placement(id: $"lineupGallery1Hair/{Key(value: bust)}").PrototypeId);
                Assert.Equal("lineupBlank", Placement(id: $"lineupGallery1Nose/{Key(value: bust)}").PrototypeId);
            }
        }
    }
    public static TheoryData<int> EveryBust() => new(values: Enumerable.Range(count: Busts, start: 0));
}
