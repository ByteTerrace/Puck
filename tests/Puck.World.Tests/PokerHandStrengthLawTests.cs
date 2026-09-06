using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the garden's heads-up fixed-limit hold'em table (games/poker.world.json) by running the SHIPPED rules
/// over a real server — never a reimplementation. The seven-card evaluator is fed authored hands through seat 1's own
/// hole mask (the rules read <c>private1.hole | table.boardMask</c>, so a whole hand may sit in the hole word) and must
/// produce the exact strength word <c>category &lt;&lt; 28 | primary &lt;&lt; 14 | secondary</c>; a full hand then
/// plays to showdown and a second folds, proving chip and card conservation, the reveal seam, the phase machine's one
/// writer, and the discard of an out-of-turn action.</summary>
public sealed class PokerHandStrengthLawTests {
    private static readonly WorldDefinition Garden = AuthoredGameFixtures.Program("poker");

    // ------------------------------------------------------------------
    // The card encoding the document authors: bit 16 * (suit - 1) + (rank - 1), suits C D H S = 1..4, ranks 2..14.
    // ------------------------------------------------------------------
    private static int Rank(char c) => c switch { 'T' => 10, 'J' => 11, 'Q' => 12, 'K' => 13, 'A' => 14, _ => c - '0' };
    private static int Suit(char c) => c switch { 'C' => 1, 'D' => 2, 'H' => 3, 'S' => 4, _ => throw new ArgumentOutOfRangeException(nameof(c)) };
    private static long Mask(string cards) => cards.Split(' ').Aggregate(0L, (m, card) => m | (1L << (16 * (Suit(card[1]) - 1) + Rank(card[0]) - 1)));
    private static long RankBit(int rank) => 1L << (rank - 1);
    private static long Index(int rank) => rank - 1;
    private static long Strength(int category, long primary, long secondary) => ((long)category << 28) | (primary << 14) | secondary;

    private static long Cell(WorldDefinition definition, string row, string key) {
        var found = WorldDefinitionRows.FindStateRow(definition.State, row);
        Assert.NotNull(found);
        return found!.Cells!.Single(c => c.Key.Value == key).Value;
    }
    private static int Count(WorldDefinition definition, string row) => WorldDefinitionRows.FindStateRow(definition.State, row)!.Cells?.Count ?? 0;
    private static void Write(WorldFixture fixture, string row, string key, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: WorldPrincipal.Console, Row: row, Key: key, Value: value, Kind: WorldDocumentWriteKind.Set
    ));
    private static void Steps(WorldFixture fixture, int count) {
        for (var index = 0; index < count; index++) { fixture.Step(); }
    }
    private static long Evaluate(WorldFixture fixture, string cards) {
        Write(fixture, "private1", "hole", Mask(cards));
        Write(fixture, "table", "evalPending", 1);
        Steps(fixture, 2);
        Assert.Equal(0L, Cell(fixture.Server.Definition, "table", "evalPending"));
        return Cell(fixture.Server.Definition, "private1", "strength");
    }

    public static TheoryData<string, string, long> Hands => new() {
        { "royal flush", "AS KS QS JS TS 2C 3D", Strength(8, Index(10), 0) },
        { "steel wheel", "AS 2S 3S 4S 5S KC QD", Strength(8, 0, 0) },
        { "quads with an ace kicker", "9C 9D 9H 9S AC 2D 3H", Strength(7, RankBit(9), Index(14)) },
        { "full house, sevens over jacks", "7C 7D 7H JS JC 2D 5H", Strength(6, Index(7), Index(11)) },
        { "two trips read as aces full of kings", "AC AD AH KS KC KD 2H", Strength(6, Index(14), Index(13)) },
        { "flush of exactly five", "2H 5H 9H JH KH 3C 4D", Strength(5, RankBit(2) | RankBit(5) | RankBit(9) | RankBit(11) | RankBit(13), 0) },
        { "flush of six drops the deuce", "2H 5H 9H JH KH 3H 4D", Strength(5, RankBit(3) | RankBit(5) | RankBit(9) | RankBit(11) | RankBit(13), 0) },
        { "straight five to nine", "5C 6D 7H 8S 9C KD 2H", Strength(4, Index(5), 0) },
        { "wheel straight", "AC 2D 3H 4S 5C KD 9H", Strength(4, 0, 0) },
        { "two straights keep the higher", "5C 6D 7H 8S 9C TD 2H", Strength(4, Index(6), 0) },
        { "trips with two kickers", "8C 8D 8H AS KC 4D 2H", Strength(3, RankBit(8), RankBit(14) | RankBit(13)) },
        { "two pair with an ace kicker", "JC JD 4H 4S AC 9D 2H", Strength(2, RankBit(11) | RankBit(4), Index(14)) },
        { "three pairs keep the top two and the best kicker", "JC JD 4H 4S 9C 9D AH", Strength(2, RankBit(11) | RankBit(9), Index(14)) },
        { "pair of queens with three kickers", "QC QD 9H 6S 4C 3D 2H", Strength(1, RankBit(12), RankBit(9) | RankBit(6) | RankBit(4)) },
        { "ace high", "AC KD 9H 7S 5C 3D 2H", Strength(0, RankBit(14) | RankBit(13) | RankBit(9) | RankBit(7) | RankBit(5), 0) },
    };

    [Theory]
    [MemberData(nameof(Hands))]
    public void TheShippedEvaluatorRanksEverySevenCardHandExactly(string name, string cards, long expected) {
        using var fixture = Fixtures.FreshServer(definition: Garden);
        Assert.Equal(expected, Evaluate(fixture, cards));
        Assert.False(string.IsNullOrEmpty(name));
    }

    // The near-miss controls: one card changed flips the category, so the word is not merely stable but discriminating.
    [Fact]
    public void OneCardSeparatesEachCategoryFromItsNearMiss() {
        using var fixture = Fixtures.FreshServer(definition: Garden);
        Assert.Equal(7, Evaluate(fixture, "9C 9D 9H 9S AC 2D 3H") >> 28);
        Assert.Equal(3, Evaluate(fixture, "9C 9D 9H 8S AC 2D 3H") >> 28); // the fourth nine gone: trips
        Assert.Equal(6, Evaluate(fixture, "7C 7D 7H JS JC 2D 5H") >> 28);
        Assert.Equal(3, Evaluate(fixture, "7C 7D 7H JS TC 2D 5H") >> 28); // the second jack gone: trips
        Assert.Equal(5, Evaluate(fixture, "2H 5H 9H JH KH 3C 4D") >> 28);
        Assert.Equal(0, Evaluate(fixture, "2H 5H 9H JH KC 3C 4D") >> 28); // the fifth heart gone: high card
        Assert.Equal(4, Evaluate(fixture, "5C 6D 7H 8S 9C KD 2H") >> 28);
        Assert.Equal(0, Evaluate(fixture, "5C 6D 7H 8S TC KD 2H") >> 28); // the nine gone: high card
        Assert.Equal(8, Evaluate(fixture, "AS 2S 3S 4S 5S KC QD") >> 28);
        Assert.Equal(4, Evaluate(fixture, "AS 2S 3S 4S 5C KC QD") >> 28); // the wheel off-suit: a plain straight
    }

    // A kicker decides between equal categories, and a better category always beats a worse one, in the plain
    // integer order of the strength word — the order the showdown rule compares.
    [Fact]
    public void TheStrengthWordOrdersHandsTheWayPokerDoes() {
        using var fixture = Fixtures.FreshServer(definition: Garden);
        Assert.True(Evaluate(fixture, "QC QD AH 6S 4C 3D 2H") > Evaluate(fixture, "QH QS KH 6C 4D 3S 2C")); // queens, ace kicker beats king kicker
        Assert.True(Evaluate(fixture, "2C 2D 3H 4S 6C 8D 9H") > Evaluate(fixture, "AC KD QH JS 9C 8D 2H")); // any pair beats ace high
        Assert.Equal(Evaluate(fixture, "AC KD 9H 7S 5C 3D 2H"), Evaluate(fixture, "AH KS 9C 7D 5S 3C 2D")); // suits never break a tie
    }

    // ------------------------------------------------------------------
    // A whole hand through the shipped phase machine: blinds, a bet and a call, checks to showdown, the reveal, the
    // award, and the collection back to a full deck. Chips and cards are conserved throughout.
    // ------------------------------------------------------------------
    private static void Act(WorldFixture fixture, int seat, long action) {
        Write(fixture, $"betAction{seat}", "act", action);
        Steps(fixture, 3);
    }

    [Fact]
    public void AHandPlaysToShowdownConservingChipsAndCardsAndRevealingTheHands() {
        using var fixture = Fixtures.FreshServer(definition: Garden);
        Write(fixture, "table", "dealRequest", 1);
        Steps(fixture, 3);

        var dealt = fixture.Server.Definition;
        Assert.Equal(1L, Cell(dealt, "phase", "street"));
        Assert.Equal(1L, Cell(dealt, "table", "button")); // the authored button (2) passed to seat 1
        Assert.Equal(1L, Cell(dealt, "table", "bettor")); // heads-up: the button posts the small blind and acts first
        Assert.Equal(2, Count(dealt, "hand1"));
        Assert.Equal(2, Count(dealt, "hand2"));
        Assert.Equal(48, Count(dealt, "deck"));
        Assert.Equal(10L, Cell(dealt, "streetBet", "1"));
        Assert.Equal(20L, Cell(dealt, "streetBet", "2"));
        Assert.Equal(30L, Cell(dealt, "table", "pot"));
        Assert.Null(WorldStateDisclosure.Compose(dealt, WorldPrincipal.Seat(1))?.SingleOrDefault(r => r.Name == "hand1")); // seat 2 cannot see hand 1

        Act(fixture, 1, 0); // the button calls
        Act(fixture, 2, 0); // the big blind checks: the flop
        Assert.Equal(2L, Cell(fixture.Server.Definition, "phase", "street"));
        Assert.Equal(3, Count(fixture.Server.Definition, "community"));
        Assert.Equal(2L, Cell(fixture.Server.Definition, "table", "bettor")); // post-flop the big blind acts first

        Act(fixture, 2, 1); // a bet of one big blind
        Act(fixture, 1, 0); // called: the turn
        Assert.Equal(3L, Cell(fixture.Server.Definition, "phase", "street"));
        Assert.Equal(4, Count(fixture.Server.Definition, "community"));
        Assert.Equal(80L, Cell(fixture.Server.Definition, "table", "pot"));

        Act(fixture, 2, 0);
        Act(fixture, 1, 0); // the river
        Assert.Equal(4L, Cell(fixture.Server.Definition, "phase", "street"));
        Assert.Equal(5, Count(fixture.Server.Definition, "community"));

        Act(fixture, 2, 0);
        Write(fixture, "betAction1", "act", 0); // the last check: the street completes, the next tick lands on the showdown
        Steps(fixture, 2);
        var shown = fixture.Server.Definition;
        Assert.Equal(5L, Cell(shown, "phase", "street"));
        Assert.Equal(1L, Cell(shown, "table", "hands"));
        Assert.Equal(0L, Cell(shown, "table", "pot"));
        Assert.Equal(2000L, Cell(shown, "stack", "1") + Cell(shown, "stack", "2"));
        var winner = Cell(shown, "table", "winner");
        var strength1 = Cell(shown, "private1", "strength");
        var strength2 = Cell(shown, "private2", "strength");
        Assert.Equal(strength1 > strength2 ? 1L : strength2 > strength1 ? 2L : 0L, winner);
        Assert.Equal(2, WorldStateDisclosure.Compose(shown, WorldPrincipal.Seat(1))?.SingleOrDefault(r => r.Name == "hand1")?.Cells.Count); // revealed to seat 2
        Assert.Equal(2, WorldStateDisclosure.Compose(shown, WorldPrincipal.Seat(0))?.SingleOrDefault(r => r.Name == "hand2")?.Cells.Count); // and hand 2 to seat 1

        Steps(fixture, 12); // the collection phase returns every card one per tick, then the table idles
        var idle = fixture.Server.Definition;
        Assert.Equal(0L, Cell(idle, "phase", "street"));
        Assert.Equal(52, Count(idle, "deck"));
        Assert.Equal(0, Count(idle, "hand1"));
        Assert.Equal(0, Count(idle, "hand2"));
        Assert.Equal(0, Count(idle, "community"));
        Assert.Equal(0L, Cell(idle, "table", "leak"));
        Assert.Equal(0L, Cell(idle, "table", "refused"));
    }

    [Fact]
    public void AFoldAwardsThePotAndAnOutOfTurnActionIsDiscardedAndCounted() {
        using var fixture = Fixtures.FreshServer(definition: Garden);
        Write(fixture, "table", "dealRequest", 1);
        Steps(fixture, 3);
        Assert.Equal(1L, Cell(fixture.Server.Definition, "table", "bettor"));

        Act(fixture, 2, 0); // seat 2 is not the bettor: discarded, counted, nothing moves
        var after = fixture.Server.Definition;
        Assert.Equal(1L, Cell(after, "table", "refused"));
        Assert.Equal(-1L, Cell(after, "betAction2", "act"));
        Assert.Equal(0L, Cell(after, "table", "acted"));
        Assert.Equal(30L, Cell(after, "table", "pot"));

        Act(fixture, 1, 1); // the button raises to two big blinds
        Assert.Equal(40L, Cell(fixture.Server.Definition, "streetBet", "1"));
        Assert.Equal(60L, Cell(fixture.Server.Definition, "table", "pot"));
        Assert.Equal(2L, Cell(fixture.Server.Definition, "table", "bettor"));

        Act(fixture, 2, 2); // the big blind folds
        Steps(fixture, 12);
        var settled = fixture.Server.Definition;
        Assert.Equal(1L, Cell(settled, "table", "winner"));
        Assert.Equal(1020L, Cell(settled, "stack", "1"));
        Assert.Equal(980L, Cell(settled, "stack", "2"));
        Assert.Equal(0L, Cell(settled, "phase", "street"));
        Assert.Equal(52, Count(settled, "deck"));
        Assert.Equal(1L, Cell(settled, "table", "hands"));
    }
}
