using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the garden's hidden-hand poker table (puck.world.json): the per-rank pair/trip/quad and the
/// straight/flush existence patterns each rank the task names reduces to, over authored 7-card hands with a
/// near-miss control per rank, and the showdown reveal's readersFrom audience widening.</summary>
public sealed class PokerHandStrengthLawTests {
    // Mirrors build_poker.py's pairAtRankN/hasTripAny/hasQuadAny/straightAny/suitAtLeast5_S — the shapes actually
    // shipped in puck.world.json's `patterns` section, over a plain 7-cell keyed row exactly like combinedByRank1/2
    // (no sort: pairAtRankN/hasTripAny/hasQuadAny/suitAtLeast5 are order-independent by construction; straightAny is
    // adjacency-based and needs its source sorted ascending by rank, which is why the hands below are authored in
    // rank order).
    private static readonly int[] Ranks = [.. Enumerable.Range(2, 13)];

    private static WorldPatternRow PairAtRank(int rank) => new(Name($"pairAtRank{rank}"), CellKind.Int,
        Symbols: [new(Name("r"), rank, rank)], Pattern: AtLeast("r", 2));

    // Adjacency-based (a run of `count` consecutive equal letters), like straightAny below — needs its source
    // sorted ascending by rank first, unlike pairAtRankN/suitAtLeast5's order-independent AtLeast.
    private static WorldPatternRow HasNAny(string name, int count) => new(Name(name), CellKind.Int,
        Symbols: [.. Ranks.Select(r => new WorldPatternSymbol(Name($"r{r}"), r, r))],
        Pattern: new WorldPatternNode.Choice([.. Ranks.Select(r => Run($"r{r}", count))]),
        MaxStates: 256);
    private static WorldPatternNode Run(string symbol, int count) => new WorldPatternNode.Sequence([
        new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()),
        new WorldPatternNode.Repeat(new WorldPatternNode.Symbol(symbol), count, count),
        new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()),
    ]);

    private static readonly WorldPatternRow HasTripAny = HasNAny("hasTripAny", 3);
    private static readonly WorldPatternRow HasQuadAny = HasNAny("hasQuadAny", 4);

    private static readonly WorldPatternRow StraightAny = new(Name("straightAny"), CellKind.Int,
        Symbols: [.. Ranks.Select(r => new WorldPatternSymbol(Name($"r{r}"), r, r))],
        Pattern: new WorldPatternNode.Choice([.. Enumerable.Range(2, 9).Select(EachPresent)]),
        MaxStates: 256);

    private static readonly WorldPatternRow SuitAtLeast5 = new(Name("suitAtLeast5_S"), CellKind.Int,
        Symbols: [new(Name("s"), 3, 3)], Pattern: AtLeast("s", 5));

    private static WorldPatternNode AtLeast(string symbol, int count) {
        var items = new List<WorldPatternNode> { new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()) };
        for (var i = 0; i < count; i++) {
            items.Add(new WorldPatternNode.Symbol(symbol));
            items.Add(new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()));
        }
        return new WorldPatternNode.Sequence(items);
    }
    private static WorldPatternNode EachPresent(int start) {
        var items = new List<WorldPatternNode> { new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()) };
        for (var r = start; r < (start + 5); r++) {
            items.Add(new WorldPatternNode.Plus(new WorldPatternNode.Symbol($"r{r}")));
        }
        items.Add(new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()));
        return new WorldPatternNode.Sequence(items);
    }

    private static long Match(WorldPatternRow pattern, params long[] word) {
        Assert.True(CompiledWorldPattern.TryCompile(pattern, out var compiled, out var reason), reason);
        return compiled!.Match(word);
    }

    [Theory]
    [InlineData(new long[] { 2, 5, 9, 13, 13, 14, 8 }, 13, 1)] // a real pair of kings...
    [InlineData(new long[] { 2, 5, 9, 12, 13, 14, 8 }, 13, 0)] // ...vs a lone king: the near-miss control.
    public void PairAtRankMatchesTwoOfARankAndRejectsOne(long[] hand, int rank, long expected) =>
        Assert.Equal(expected, Match(PairAtRank(rank), hand));

    [Theory]
    [InlineData(new long[] { 2, 5, 9, 9, 9, 14, 8 }, 1)] // three of a kind — the trip rank appears three times...
    [InlineData(new long[] { 2, 5, 9, 9, 12, 14, 8 }, 0)] // ...vs only a pair: the near-miss control.
    public void HasTripAnyMatchesExistenceOfAnyThreeOfARankAndRejectsAPairAlone(long[] hand, long expected) =>
        Assert.Equal(expected, Match(HasTripAny, hand));

    [Theory]
    [InlineData(new long[] { 2, 5, 9, 9, 9, 9, 8 }, 1)] // four of a kind...
    [InlineData(new long[] { 2, 5, 9, 9, 9, 14, 8 }, 0)] // ...vs the same rank stuck at three: the near-miss control.
    public void HasQuadAnyMatchesExistenceOfAnyFourOfARankAndRejectsAThreeOfAKind(long[] hand, long expected) =>
        Assert.Equal(expected, Match(HasQuadAny, hand));

    [Theory]
    [InlineData(new long[] { 2, 3, 4, 5, 6, 9, 13 }, 1)] // five ranks in a row (2-3-4-5-6), sorted ascending...
    [InlineData(new long[] { 2, 3, 4, 6, 7, 9, 13 }, 0)] // ...vs the same span missing rank 5: the near-miss control.
    public void StraightAnyMatchesFiveConsecutiveRanksAndRejectsAGappedRun(long[] sortedHand, long expected) =>
        Assert.Equal(expected, Match(StraightAny, sortedHand));

    [Theory]
    [InlineData(new long[] { 3, 0, 3, 1, 3, 3, 3 }, 1)] // five suit-3 cards, in whatever order they were dealt...
    [InlineData(new long[] { 3, 0, 3, 1, 3, 3, 2 }, 0)] // ...vs only four of them: the near-miss control.
    public void SuitAtLeastFiveMatchesFiveOfASuitAnywhereAndRejectsFour(long[] hand, long expected) =>
        Assert.Equal(expected, Match(SuitAtLeast5, hand));

    // The full house/two-pair distinction the live strength row (WorldRuleWorkBudget's ceiling — see
    // build_poker_rules.py's poker-derive-facts remarks — affords only pairCount, not trip/quad/straight/flush,
    // for the live rows) rests on: a genuine trip's OWN rank always also satisfies pairAtRank (3 >= 2), so
    // "full house" is trip-exists AND pairCount >= 2, never pairCount >= 2 alone — the second control below is
    // exactly the case that distinction exists to rule out.
    [Fact]
    public void FullHouseNeedsATripPlusASeparatePairNotJustAnyTwoPairedRanks() {
        long[] fullHouse = [7, 7, 7, 11, 11, 2, 5]; // trip sevens, pair jacks
        long[] tripOnly = [7, 7, 7, 11, 2, 5, 9]; // trip sevens, no second pair — the near-miss control

        Assert.Equal(1, Match(HasTripAny, fullHouse));
        Assert.Equal(2, PairCount(fullHouse)); // the trip's own rank (absorption) plus the genuine jack pair
        Assert.Equal(1, Match(HasTripAny, tripOnly));
        Assert.Equal(1, PairCount(tripOnly)); // only the trip's own rank — no full house
    }

    private static long PairCount(long[] hand) => Ranks.Sum(r => Match(PairAtRank(r), hand));

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
