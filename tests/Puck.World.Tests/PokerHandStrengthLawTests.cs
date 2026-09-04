using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the garden's hidden-hand poker table (puck.world.json) by compiling and matching the shipped
/// `patterns` rows themselves (never a reimplementation), over authored 7-card hands with a near-miss control per
/// rank, and the showdown reveal's readersFrom audience widening. The live `strength1`/`strength2` rows fold only
/// `pairAny` — the garden's chess/tabletop rules already spend nearly the whole document work-budget ceiling
/// (`world.budget`), leaving no headroom for trip/quad/straight/flush once the deal, the two rank sorts, and the
/// `rank`/`suit` attribute rows' privacy-required `keysFrom` cost are paid — see puck.world.json's poker-strength1/2
/// remarks and docs/campaign.md. `hasTripAny`, `hasQuadAny`, `straightAny`, `pairAtRank2..14`, and
/// `suitAtLeast5_0..3` remain shipped, correct, and reachable via `world.match` regardless.</summary>
public sealed class PokerHandStrengthLawTests {
    private static readonly WorldDefinition Garden = LoadGarden();

    private static WorldDefinition LoadGarden() {
        var path = FindGardenWorld();
        Assert.True(WorldDefinitionFileSource.TryLoad(path, out var definition, out _, out var reason), reason);
        return definition!;
    }

    // Walks up from the test assembly's run directory to the repo root (the directory carrying Puck.slnx), then
    // down to the garden's own shipped document — the same file `puck.world.json` names everywhere else.
    private static string FindGardenWorld() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Puck.World", "Assets", "worlds", "puck.world.json");
    }

    private static WorldPatternRow Find(string name) =>
        Garden.Patterns.FirstOrDefault(p => p.Name.Value == name)
        ?? throw new InvalidOperationException($"puck.world.json carries no pattern '{name}'");

    private static long Match(string patternName, params long[] word) {
        Assert.True(CompiledWorldPattern.TryCompile(Find(patternName), out var compiled, out var reason), reason);
        return compiled!.Match(word);
    }

    [Theory]
    [InlineData(new long[] { 2, 5, 8, 9, 12, 13, 13 }, "pairAtRank13", 1)] // a real pair of kings, sorted...
    [InlineData(new long[] { 2, 5, 8, 9, 12, 13, 14 }, "pairAtRank13", 0)] // ...vs a lone king: the near-miss control.
    public void PairAtRankMatchesTwoOfARankAndRejectsOne(long[] hand, string pattern, long expected) =>
        Assert.Equal(expected, Match(pattern, hand));

    [Theory]
    [InlineData(new long[] { 2, 5, 8, 9, 9, 9, 14 }, 1)] // three of a kind, sorted ascending...
    [InlineData(new long[] { 2, 5, 8, 9, 9, 12, 14 }, 0)] // ...vs only a pair: the near-miss control.
    public void HasTripAnyMatchesExistenceOfAnyThreeOfARankAndRejectsAPairAlone(long[] sortedHand, long expected) =>
        Assert.Equal(expected, Match("hasTripAny", sortedHand));

    [Theory]
    [InlineData(new long[] { 2, 5, 8, 9, 9, 9, 9 }, 1)] // four of a kind, sorted ascending...
    [InlineData(new long[] { 2, 5, 8, 9, 9, 9, 14 }, 0)] // ...vs the same rank stuck at three: the near-miss control.
    public void HasQuadAnyMatchesExistenceOfAnyFourOfARankAndRejectsAThreeOfAKind(long[] sortedHand, long expected) =>
        Assert.Equal(expected, Match("hasQuadAny", sortedHand));

    [Theory]
    [InlineData(new long[] { 2, 3, 4, 5, 6, 9, 13 }, 1)] // five ranks in a row (2-3-4-5-6), sorted ascending...
    [InlineData(new long[] { 2, 3, 4, 6, 7, 9, 13 }, 0)] // ...vs the same span missing rank 5: the near-miss control.
    public void StraightAnyMatchesFiveConsecutiveRanksAndRejectsAGappedRun(long[] sortedHand, long expected) =>
        Assert.Equal(expected, Match("straightAny", sortedHand));

    [Theory]
    [InlineData(new long[] { 4, 1, 4, 2, 4, 4, 4 }, "suitAtLeast5_3", 1)] // five suit-4 (S) cards, deal order...
    [InlineData(new long[] { 4, 1, 4, 2, 4, 4, 3 }, "suitAtLeast5_3", 0)] // ...vs only four of them: the control.
    public void SuitAtLeastFiveMatchesFiveOfASuitAnywhereAndRejectsFour(long[] hand, string pattern, long expected) =>
        Assert.Equal(expected, Match(pattern, hand));

    // Neither the live strength rows nor a dedicated pattern compute "full house" or "two pair" (the work budget
    // has no room for a per-rank-pair enumeration or a pairCount tally live — see the class remarks), but the
    // shipped pairAtRank2..14 patterns still let a follow-up change compose the same distinction: a genuine trip's
    // own rank always also satisfies pairAtRank (3 >= 2), so "full house" is trip-exists AND a second distinct
    // pairAtRank, never any two paired ranks alone.
    [Fact]
    public void FullHouseStillNeedsATripPlusASeparatePairNotJustAnyTwoPairedRanks() {
        long[] fullHouse = [2, 5, 7, 7, 7, 11, 11]; // trip sevens, pair jacks, sorted ascending
        long[] tripOnly = [2, 5, 7, 7, 7, 9, 11]; // trip sevens, no second pair — the near-miss control

        Assert.Equal(1, Match("hasTripAny", fullHouse));
        Assert.Equal(2, PairCount(fullHouse)); // the trip's own rank (absorption) plus the genuine jack pair
        Assert.Equal(1, Match("hasTripAny", tripOnly));
        Assert.Equal(1, PairCount(tripOnly)); // only the trip's own rank — no full house
    }

    private static long PairCount(long[] hand) =>
        Enumerable.Range(2, 13).Sum(rank => Match($"pairAtRank{rank}", hand));

    // ------------------------------------------------------------------
    // The showdown reveal: hand1 is invisible to seat2 until a rule (or, in
    // this proof, a direct write) puts seat2's token into hand1's
    // readersFrom row — the exact mechanism poker-showdown-reveal uses.
    // ------------------------------------------------------------------
    [Fact]
    public void ReadersFromWidensAHiddenHandsAudienceOnlyAfterTheTokenIsWritten() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [
                new(Name("cards"), CellKind.Int, Tokens: new(Capacity: 2), Cells: [Cell("AS", 0), Cell("KS", 0)]),
                new(Name("hand1"), CellKind.Bool, Zone: new("cards"), Capacity: 2,
                    Cells: [Cell("AS", 1), Cell("KS", 1)],
                    Visibility: new(Readers: ["seat1"], ReadersFrom: "audience1")),
                new(Name("audience1"), CellKind.Text, Capacity: 1),
            ]),
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);

        Assert.Null(WorldStateDisclosure.Compose(definition, WorldPrincipal.Seat(1))); // seat2: nothing disclosed yet
        Assert.NotNull(WorldStateDisclosure.Compose(definition, WorldPrincipal.Seat(0))); // seat1: its own hand

        var revealed = definition.WithWorldState([.. definition.State.Select(r => r.Name.Value == "audience1"
            ? r with { Cells = [new WorldStateCell(Name("0"), 0, Text: WorldPrincipal.Seat(1).Describe())] }
            : r)]);

        var seenBySeat2 = WorldStateDisclosure.Compose(revealed, WorldPrincipal.Seat(1));
        var hand1 = Assert.Single(seenBySeat2!, row => row.Name == "hand1");
        Assert.Equal(2, hand1.Cells.Count);
        Assert.Contains(hand1.Cells, c => c.Key == "AS");
        Assert.Contains(hand1.Cells, c => c.Key == "KS");
    }

    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
}
