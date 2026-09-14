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
    private static readonly WorldDefinition Garden = AuthoredGameFixtures.Program(module: "poker");

    // ------------------------------------------------------------------
    // A whole hand through the shipped phase machine: blinds, a bet and a call, checks to showdown, the reveal, the
    // award, and the collection back to a full deck. Chips and cards are conserved throughout.
    // ------------------------------------------------------------------
    private static void Act(WorldFixture fixture, int seat, long action) {
        Write(
            fixture: fixture,
            key: "act",
            row: $"betAction{seat}",
            value: action
        );
        Steps(
            count: 3,
            fixture: fixture
        );
    }
    private static long Cell(WorldDefinition definition, string row, string key) {
        var found = WorldDefinitionRows.FindStateRow(
            definition.State,
            row
        );

        Assert.NotNull(@object: found);
        return found!.Cells!.Single(predicate: c => (c.Key.Value == key)).Value;
    }
    private static int Count(WorldDefinition definition, string row) => (WorldDefinitionRows.FindStateRow(
        definition.State,
        row
    )!.Cells?.Count ?? 0);
    private static long Evaluate(WorldFixture fixture, string cards) {
        Write(
            fixture,
            "private1",
            "hole",
            Mask(cards: cards)
        );
        Write(
            fixture: fixture,
            key: "evalPending",
            row: "table",
            value: 1
        );
        Steps(
            count: 2,
            fixture: fixture
        );
        Assert.Equal(
            0L,
            Cell(
                fixture.Server.Definition,
                "table",
                "evalPending"
            )
        );
        return Cell(
            fixture.Server.Definition,
            "private1",
            "strength"
        );
    }
    private static long Index(int rank) => (rank - 1);
    private static long Mask(string cards) => cards.Split(' ').Aggregate(
        0L,
        (m, card) => m | (1L << (((16 * (Suit(c: card[1]) - 1)) + Rank(c: card[0])) - 1))
    );
    // ------------------------------------------------------------------
    // The card encoding the document authors: bit 16 * (suit - 1) + (rank - 1), suits C D H S = 1..4, ranks 2..14.
    // ------------------------------------------------------------------
    private static int Rank(char c) => c switch { 'T' => 10, 'J' => 11, 'Q' => 12, 'K' => 13, 'A' => 14, _ => (c - '0') };
    private static long RankBit(int rank) => (1L << (rank - 1));
    private static void Steps(WorldFixture fixture, int count) {
        for (var index = 0; (index < count); index++) { fixture.Step(); }
    }
    private static long Strength(int category, long primary, long secondary) => (((long)category) << 28) | (primary << 14) | secondary;
    private static int Suit(char c) => c switch { 'C' => 1, 'D' => 2, 'H' => 3, 'S' => 4, _ => throw new ArgumentOutOfRangeException(paramName: nameof(c)) };
    private static void Write(WorldFixture fixture, string row, string key, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: WorldPrincipal.Console,
        Row: row,
        Key: key,
        Value: value,
        Kind: WorldDocumentWriteKind.Set
    ));

    [Fact]
    public void AFoldAwardsThePotAndAnOutOfTurnActionIsDiscardedAndCounted() {
        using var fixture = Fixtures.FreshServer(definition: Garden);

        Write(
            fixture: fixture,
            key: "dealRequest",
            row: "table",
            value: 1
        );
        Steps(
            count: 3,
            fixture: fixture
        );
        Assert.Equal(
            1L,
            Cell(
                fixture.Server.Definition,
                "table",
                "bettor"
            )
        );

        Act(
            action: 0,
            fixture: fixture,
            seat: 2
        ); // seat 2 is not the bettor: discarded, counted, nothing moves
        var after = fixture.Server.Definition;

        Assert.Equal(
            1L,
            Cell(
                definition: after,
                key: "refused",
                row: "table"
            )
        );
        Assert.Equal(
            -1L,
            Cell(
                definition: after,
                key: "act",
                row: "betAction2"
            )
        );
        Assert.Equal(
            0L,
            Cell(
                definition: after,
                key: "acted",
                row: "table"
            )
        );
        Assert.Equal(
            30L,
            Cell(
                definition: after,
                key: "pot",
                row: "table"
            )
        );

        Act(
            action: 1,
            fixture: fixture,
            seat: 1
        ); // the button raises to two big blinds
        Assert.Equal(
            40L,
            Cell(
                fixture.Server.Definition,
                "streetBet",
                "1"
            )
        );
        Assert.Equal(
            60L,
            Cell(
                fixture.Server.Definition,
                "table",
                "pot"
            )
        );
        Assert.Equal(
            2L,
            Cell(
                fixture.Server.Definition,
                "table",
                "bettor"
            )
        );

        Act(
            action: 2,
            fixture: fixture,
            seat: 2
        ); // the big blind folds
        Steps(
            count: 12,
            fixture: fixture
        );
        var settled = fixture.Server.Definition;

        Assert.Equal(
            1L,
            Cell(
                definition: settled,
                key: "winner",
                row: "table"
            )
        );
        Assert.Equal(
            1020L,
            Cell(
                definition: settled,
                key: "1",
                row: "stack"
            )
        );
        Assert.Equal(
            980L,
            Cell(
                definition: settled,
                key: "2",
                row: "stack"
            )
        );
        Assert.Equal(
            0L,
            Cell(
                definition: settled,
                key: "street",
                row: "phase"
            )
        );
        Assert.Equal(
            52,
            Count(
                definition: settled,
                row: "deck"
            )
        );
        Assert.Equal(
            1L,
            Cell(
                definition: settled,
                key: "hands",
                row: "table"
            )
        );
    }
    [Fact]
    public void AHandPlaysToShowdownConservingChipsAndCardsAndRevealingTheHands() {
        using var fixture = Fixtures.FreshServer(definition: Garden);

        Write(
            fixture: fixture,
            key: "dealRequest",
            row: "table",
            value: 1
        );
        Steps(
            count: 3,
            fixture: fixture
        );

        var dealt = fixture.Server.Definition;

        Assert.Equal(
            1L,
            Cell(
                definition: dealt,
                key: "street",
                row: "phase"
            )
        );
        Assert.Equal(
            1L,
            Cell(
                definition: dealt,
                key: "button",
                row: "table"
            )
        ); // the authored button (2) passed to seat 1
        Assert.Equal(
            1L,
            Cell(
                definition: dealt,
                key: "bettor",
                row: "table"
            )
        ); // heads-up: the button posts the small blind and acts first
        Assert.Equal(
            2,
            Count(
                definition: dealt,
                row: "hand1"
            )
        );
        Assert.Equal(
            2,
            Count(
                definition: dealt,
                row: "hand2"
            )
        );
        Assert.Equal(
            48,
            Count(
                definition: dealt,
                row: "deck"
            )
        );
        Assert.Equal(
            10L,
            Cell(
                definition: dealt,
                key: "1",
                row: "streetBet"
            )
        );
        Assert.Equal(
            20L,
            Cell(
                definition: dealt,
                key: "2",
                row: "streetBet"
            )
        );
        Assert.Equal(
            30L,
            Cell(
                definition: dealt,
                key: "pot",
                row: "table"
            )
        );
        Assert.Null(@object: WorldStateDisclosure.Compose(
            definition: dealt,
            recipient: WorldPrincipal.Seat(slot: 1)
        )?.SingleOrDefault(predicate: r => (r.Name == "hand1"))); // seat 2 cannot see hand 1

        Act(
            action: 0,
            fixture: fixture,
            seat: 1
        ); // the button calls
        Act(
            action: 0,
            fixture: fixture,
            seat: 2
        ); // the big blind checks: the flop
        Assert.Equal(
            2L,
            Cell(
                fixture.Server.Definition,
                "phase",
                "street"
            )
        );
        Assert.Equal(
            3,
            Count(
                definition: fixture.Server.Definition,
                row: "community"
            )
        );
        Assert.Equal(
            2L,
            Cell(
                fixture.Server.Definition,
                "table",
                "bettor"
            )
        ); // post-flop the big blind acts first

        Act(
            action: 1,
            fixture: fixture,
            seat: 2
        ); // a bet of one big blind
        Act(
            action: 0,
            fixture: fixture,
            seat: 1
        ); // called: the turn
        Assert.Equal(
            3L,
            Cell(
                fixture.Server.Definition,
                "phase",
                "street"
            )
        );
        Assert.Equal(
            4,
            Count(
                definition: fixture.Server.Definition,
                row: "community"
            )
        );
        Assert.Equal(
            80L,
            Cell(
                fixture.Server.Definition,
                "table",
                "pot"
            )
        );

        Act(
            action: 0,
            fixture: fixture,
            seat: 2
        );
        Act(
            action: 0,
            fixture: fixture,
            seat: 1
        ); // the river
        Assert.Equal(
            4L,
            Cell(
                fixture.Server.Definition,
                "phase",
                "street"
            )
        );
        Assert.Equal(
            5,
            Count(
                definition: fixture.Server.Definition,
                row: "community"
            )
        );

        Act(
            action: 0,
            fixture: fixture,
            seat: 2
        );
        Write(
            fixture: fixture,
            key: "act",
            row: "betAction1",
            value: 0
        ); // the last check: the street completes, the next tick lands on the showdown
        Steps(
            count: 2,
            fixture: fixture
        );
        var shown = fixture.Server.Definition;

        Assert.Equal(
            5L,
            Cell(
                definition: shown,
                key: "street",
                row: "phase"
            )
        );
        Assert.Equal(
            1L,
            Cell(
                definition: shown,
                key: "hands",
                row: "table"
            )
        );
        Assert.Equal(
            0L,
            Cell(
                definition: shown,
                key: "pot",
                row: "table"
            )
        );
        Assert.Equal(
            2000L,
            (Cell(
                definition: shown,
                key: "1",
                row: "stack"
            ) + Cell(
                definition: shown,
                key: "2",
                row: "stack"
            ))
        );
        var winner = Cell(
            definition: shown,
            key: "winner",
            row: "table"
        );
        var strength1 = Cell(
            definition: shown,
            key: "strength",
            row: "private1"
        );
        var strength2 = Cell(
            definition: shown,
            key: "strength",
            row: "private2"
        );

        Assert.Equal(
            actual: winner,
            expected: ((strength1 > strength2)
            ? 1L
            : ((strength2 > strength1)
                ? 2L
                : 0L))
        );
        Assert.Equal(
            2,
            WorldStateDisclosure.Compose(
                definition: shown,
                recipient: WorldPrincipal.Seat(slot: 1)
            )?.SingleOrDefault(predicate: r => (r.Name == "hand1"))?.Cells.Count
        ); // revealed to seat 2
        Assert.Equal(
            2,
            WorldStateDisclosure.Compose(
                definition: shown,
                recipient: WorldPrincipal.Seat(slot: 0)
            )?.SingleOrDefault(predicate: r => (r.Name == "hand2"))?.Cells.Count
        ); // and hand 2 to seat 1

        Steps(
            count: 12,
            fixture: fixture
        ); // the collection phase returns every card one per tick, then the table idles
        var idle = fixture.Server.Definition;

        Assert.Equal(
            0L,
            Cell(
                definition: idle,
                key: "street",
                row: "phase"
            )
        );
        Assert.Equal(
            52,
            Count(
                definition: idle,
                row: "deck"
            )
        );
        Assert.Equal(
            0,
            Count(
                definition: idle,
                row: "hand1"
            )
        );
        Assert.Equal(
            0,
            Count(
                definition: idle,
                row: "hand2"
            )
        );
        Assert.Equal(
            0,
            Count(
                definition: idle,
                row: "community"
            )
        );
        Assert.Equal(
            0L,
            Cell(
                definition: idle,
                key: "leak",
                row: "table"
            )
        );
        Assert.Equal(
            0L,
            Cell(
                definition: idle,
                key: "refused",
                row: "table"
            )
        );
    }
    // The near-miss controls: one card changed flips the category, so the word is not merely stable but discriminating.
    [Fact]
    public void OneCardSeparatesEachCategoryFromItsNearMiss() {
        using var fixture = Fixtures.FreshServer(definition: Garden);

        Assert.Equal(
            7,
            (Evaluate(
                cards: "9C 9D 9H 9S AC 2D 3H",
                fixture: fixture
            ) >> 28)
        );
        Assert.Equal(
            3,
            (Evaluate(
                cards: "9C 9D 9H 8S AC 2D 3H",
                fixture: fixture
            ) >> 28)
        ); // the fourth nine gone: trips
        Assert.Equal(
            6,
            (Evaluate(
                cards: "7C 7D 7H JS JC 2D 5H",
                fixture: fixture
            ) >> 28)
        );
        Assert.Equal(
            3,
            (Evaluate(
                cards: "7C 7D 7H JS TC 2D 5H",
                fixture: fixture
            ) >> 28)
        ); // the second jack gone: trips
        Assert.Equal(
            5,
            (Evaluate(
                cards: "2H 5H 9H JH KH 3C 4D",
                fixture: fixture
            ) >> 28)
        );
        Assert.Equal(
            0,
            (Evaluate(
                cards: "2H 5H 9H JH KC 3C 4D",
                fixture: fixture
            ) >> 28)
        ); // the fifth heart gone: high card
        Assert.Equal(
            4,
            (Evaluate(
                cards: "5C 6D 7H 8S 9C KD 2H",
                fixture: fixture
            ) >> 28)
        );
        Assert.Equal(
            0,
            (Evaluate(
                cards: "5C 6D 7H 8S TC KD 2H",
                fixture: fixture
            ) >> 28)
        ); // the nine gone: high card
        Assert.Equal(
            8,
            (Evaluate(
                cards: "AS 2S 3S 4S 5S KC QD",
                fixture: fixture
            ) >> 28)
        );
        Assert.Equal(
            4,
            (Evaluate(
                cards: "AS 2S 3S 4S 5C KC QD",
                fixture: fixture
            ) >> 28)
        ); // the wheel off-suit: a plain straight
    }
    [MemberData(nameof(Hands))]
    [Theory]
    public void TheShippedEvaluatorRanksEverySevenCardHandExactly(string name, string cards, long expected) {
        using var fixture = Fixtures.FreshServer(definition: Garden);

        Assert.Equal(
            expected,
            Evaluate(
                cards: cards,
                fixture: fixture
            )
        );
        Assert.False(condition: string.IsNullOrEmpty(value: name));
    }
    // A kicker decides between equal categories, and a better category always beats a worse one, in the plain
    // integer order of the strength word — the order the showdown rule compares.
    [Fact]
    public void TheStrengthWordOrdersHandsTheWayPokerDoes() {
        using var fixture = Fixtures.FreshServer(definition: Garden);

        Assert.True(condition: (Evaluate(
            cards: "QC QD AH 6S 4C 3D 2H",
            fixture: fixture
        ) > Evaluate(
            cards: "QH QS KH 6C 4D 3S 2C",
            fixture: fixture
        ))); // queens, ace kicker beats king kicker
        Assert.True(condition: (Evaluate(
            cards: "2C 2D 3H 4S 6C 8D 9H",
            fixture: fixture
        ) > Evaluate(
            cards: "AC KD QH JS 9C 8D 2H",
            fixture: fixture
        ))); // any pair beats ace high
        Assert.Equal(
            Evaluate(
                cards: "AC KD 9H 7S 5C 3D 2H",
                fixture: fixture
            ),
            Evaluate(
                cards: "AH KS 9C 7D 5S 3C 2D",
                fixture: fixture
            )
        ); // suits never break a tie
    }

    public static TheoryData<string, string, long> Hands => new() {
        { "royal flush", "AS KS QS JS TS 2C 3D", Strength(
        category: 8,
        primary: Index(rank: 10),
        secondary: 0
    ) },
        { "steel wheel", "AS 2S 3S 4S 5S KC QD", Strength(
        category: 8,
        primary: 0,
        secondary: 0
    ) },
        { "quads with an ace kicker", "9C 9D 9H 9S AC 2D 3H", Strength(
        category: 7,
        primary: RankBit(rank: 9),
        secondary: Index(rank: 14)
    ) },
        { "full house, sevens over jacks", "7C 7D 7H JS JC 2D 5H", Strength(
        category: 6,
        primary: Index(rank: 7),
        secondary: Index(rank: 11)
    ) },
        { "two trips read as aces full of kings", "AC AD AH KS KC KD 2H", Strength(
        category: 6,
        primary: Index(rank: 14),
        secondary: Index(rank: 13)
    ) },
        { "flush of exactly five", "2H 5H 9H JH KH 3C 4D", Strength(
        category: 5,
        primary: RankBit(rank: 2) | RankBit(rank: 5) | RankBit(rank: 9) | RankBit(rank: 11) | RankBit(rank: 13),
        secondary: 0
    ) },
        { "flush of six drops the deuce", "2H 5H 9H JH KH 3H 4D", Strength(
        category: 5,
        primary: RankBit(rank: 3) | RankBit(rank: 5) | RankBit(rank: 9) | RankBit(rank: 11) | RankBit(rank: 13),
        secondary: 0
    ) },
        { "straight five to nine", "5C 6D 7H 8S 9C KD 2H", Strength(
        category: 4,
        primary: Index(rank: 5),
        secondary: 0
    ) },
        { "wheel straight", "AC 2D 3H 4S 5C KD 9H", Strength(
        category: 4,
        primary: 0,
        secondary: 0
    ) },
        { "two straights keep the higher", "5C 6D 7H 8S 9C TD 2H", Strength(
        category: 4,
        primary: Index(rank: 6),
        secondary: 0
    ) },
        { "trips with two kickers", "8C 8D 8H AS KC 4D 2H", Strength(
        category: 3,
        primary: RankBit(rank: 8),
        secondary: RankBit(rank: 14) | RankBit(rank: 13)
    ) },
        { "two pair with an ace kicker", "JC JD 4H 4S AC 9D 2H", Strength(
        category: 2,
        primary: RankBit(rank: 11) | RankBit(rank: 4),
        secondary: Index(rank: 14)
    ) },
        { "three pairs keep the top two and the best kicker", "JC JD 4H 4S 9C 9D AH", Strength(
        category: 2,
        primary: RankBit(rank: 11) | RankBit(rank: 9),
        secondary: Index(rank: 14)
    ) },
        { "pair of queens with three kickers", "QC QD 9H 6S 4C 3D 2H", Strength(
        category: 1,
        primary: RankBit(rank: 12),
        secondary: RankBit(rank: 9) | RankBit(rank: 6) | RankBit(rank: 4)
    ) },
        { "ace high", "AC KD 9H 7S 5C 3D 2H", Strength(
        category: 0,
        primary: RankBit(rank: 14) | RankBit(rank: 13) | RankBit(rank: 9) | RankBit(rank: 7) | RankBit(rank: 5),
        secondary: 0
    ) },
    };
}
