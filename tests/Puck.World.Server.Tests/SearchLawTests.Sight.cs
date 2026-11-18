using Puck.Commands;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class SearchLawTests {
    // The fixture's one job, planning over the authority's true state.
    private static WorldDefinition AsAuthority(WorldDefinition definition) => definition with {
        SearchRaw = new WorldSearchSection(Jobs: [.. definition.Search.Jobs!.Select(job => job with { Actor = Principal.World })]),
    };

    // A judge rule draws a private site when token a steps onto cell 1, and the score adds the draw to a hand only seat 2
    // reads. Planning as seat 1, the job imagines the hand and draws from its own sampler: it chooses the draw, its
    // answer is the same whatever the hand truly holds, and the authority's stream is never read.
    private static (long Score, long Target, ulong Position, long Reads) DecideOverAHiddenHand(long hand, Principal actor, string score = "roll + hand") {
        var definition = MiniNegamaxWorld(1);
        var drawn = new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw), Timing: DrawTiming.Event, PrivateStream: true);
        var audience = new StateVisibility(Readers: [actor.Kind == PrincipalKind.World ? "seat2" : actor.Describe()]);

        definition = definition.WithWorldState([
            .. definition.AuthoredState.Select(row => (row.Name.Value == "best") ? row with { Visibility = (actor.Kind == PrincipalKind.World) ? new StateVisibility(Readers: []) : audience } : row),
            StateFixtures.IntSlot("roll") with { Draw = drawn },
            StateFixtures.IntSlot("hand", hand) with { Visibility = new StateVisibility(Readers: ["seat2"]), Min = 1, Max = 52 },
        ]) with {
            Rules = [
                new WorldRule(Name: CellName.Parse("drawOnOne"), Gate: new ActionPredicate.All([
                    new ActionPredicate.CompareState(State: RuleFacts.SearchPly, Comparison: ExpressionOp.Greater, Value: 0),
                    new ActionPredicate.CompareState(State: "pieceCell", Key: "a", Comparison: ExpressionOp.Equal, Value: 1),
                ]), Effects: [new ActionEffect.Generate("roll")]),
                definition.Rules![0] with { Gate = new ActionPredicate.CompareState(State: RuleFacts.SearchPly, Comparison: ExpressionOp.Greater, Value: 0) },
            ],
            SearchRaw = new WorldSearchSection(Jobs: [definition.Search.Jobs![0] with { Actor = actor, Score = ExpressionProgram.Parse(score) }]),
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        var keys = WorldPrivateDrawKeys.Create();

        using var fixture = Fixtures.FreshServer(definition, drawKeys: keys);
        var reads = keys.Reads;

        for (var tick = 0; (tick < 64) && !fixture.Server.SearchStatus()[0].Done; tick++) { fixture.Step(); }

        var status = fixture.Server.SearchStatus()[0];

        Assert.True(status.Done);
        Assert.True(status.Count > 0);
        Assert.Equal(status.BestScore, fixture.KeyedValue(key: "score", row: "best"));
        Assert.Equal(fixture.KeyedValue(key: "$value", row: "hand"), hand);
        return (status.BestScore, status.BestTarget, fixture.Server.Search.PositionKey(0), keys.Reads - reads);
    }

    [Fact]
    public void AnActorsSearchChoosesToDrawWithItsOwnSamplerAndNeverSeesTheHiddenHand() {
        var seat1 = Principal.Seat(0);
        var answer = DecideOverAHiddenHand(11, seat1);

        Assert.Equal(1L, answer.Target);
        Assert.Equal(0L, answer.Reads);
        Assert.Equal(answer, DecideOverAHiddenHand(37, seat1));
        Assert.Equal(answer, DecideOverAHiddenHand(52, seat1));
    }

    [Fact]
    public void AnAuthoritySearchReadsTheTrueHandAndStillDrawsWithItsOwnSampler() {
        var drawing = DecideOverAHiddenHand(11, Principal.World);

        Assert.Equal(1L, drawing.Target);
        Assert.Equal(0L, drawing.Reads);
        Assert.Equal(11L, DecideOverAHiddenHand(11, Principal.World, score: "hand").Score);
        Assert.Equal(37L, DecideOverAHiddenHand(37, Principal.World, score: "hand").Score);
        // The same job planning as seat 1 imagines the hand: one value whatever the hand holds, from the plan's seed.
        var imagined = DecideOverAHiddenHand(11, Principal.Seat(0), score: "hand").Score;

        Assert.InRange(imagined, 1L, 52L);
        Assert.Equal(imagined, DecideOverAHiddenHand(37, Principal.Seat(0), score: "hand").Score);
    }

    // A hand is a pile whose members are the secret. Planning as seat 1, the job sees a hand of the size the pile's
    // policy discloses, dealt from cards it sees in no pile, so which cards seat 2 truly holds changes nothing it reads.
    private static (long Score, ulong Position) DecideOverAHiddenPile(HiddenCells hidden, params string[] held) {
        var definition = MiniNegamaxWorld(1);

        definition = definition.WithWorldState([
            .. definition.AuthoredState.Select(row => (row.Name.Value == "best") ? row with { Visibility = new StateVisibility(Readers: ["seat1"]) } : row),
            new WorldStateRow(CellName.Parse("cards"), CellKind.Int, Capacity: 6,
                Cells: [.. Enumerable.Range(1, 6).Select(card => new StateCell(CellName.Parse($"c{card}"), CellValue.Int(card)))]),
            new WorldStateRow(CellName.Parse("shown"), CellKind.Bool, Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true), Capacity: 6,
                Cells: [new StateCell(CellName.Parse("c6"), CellValue.Bool(true))]),
            // Every reader reads the pile, and each card in it only seat 2: the policy says what the others learn.
            new WorldStateRow(CellName.Parse("hand2"), CellKind.Bool, Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true), Capacity: 6,
                Cells: [.. held.Select(card => new StateCell(CellName.Parse(card), CellValue.Bool(true), Visibility: new StateVisibility(Readers: ["seat2"])))],
                Visibility: new StateVisibility(Hidden: hidden), CellVisibility: true),
        ]) with {
            SearchRaw = new WorldSearchSection(Jobs: [definition.Search.Jobs![0] with {
                Actor = Principal.Seat(0),
                Score = ExpressionProgram.Parse("count(hand2) * 10 + count(shown)"),
            }]),
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        using var fixture = Fixtures.FreshServer(definition);

        for (var tick = 0; (tick < 64) && !fixture.Server.SearchStatus()[0].Done; tick++) { fixture.Step(); }
        Assert.True(fixture.Server.SearchStatus()[0].Done);
        Assert.Equal(held.Length, fixture.Server.Arena.CellCount(fixture.Server.Arena.Catalog.TryResolve(StateLane.Document, "hand2", out var hand) ? hand.Ordinal : -1));
        return (fixture.Server.SearchStatus()[0].BestScore, fixture.Server.Search.PositionKey(0));
    }

    [Fact]
    public void AnActorsSearchDealsAHiddenPileFromWhatItMaySeeAtTheSizeItIsTold() {
        var counted = DecideOverAHiddenPile(HiddenCells.Count, "c1", "c2", "c3");

        Assert.Equal(31L, counted.Score);
        Assert.Equal(counted, DecideOverAHiddenPile(HiddenCells.Count, "c3", "c4", "c5"));
        var placed = DecideOverAHiddenPile(HiddenCells.Placeholder, "c2", "c4", "c5");

        Assert.Equal(31L, placed.Score);
        Assert.Equal(placed, DecideOverAHiddenPile(HiddenCells.Placeholder, "c1", "c3", "c5"));

        // Under Omit even whether a card is hidden is withheld, so the job imagines none, whatever seat 2 holds.
        var omitted = DecideOverAHiddenPile(HiddenCells.Omit, "c1");

        Assert.Equal(1L, omitted.Score);
        Assert.Equal(omitted, DecideOverAHiddenPile(HiddenCells.Omit, "c1", "c2", "c3", "c4", "c5"));
        Assert.Equal(omitted, DecideOverAHiddenPile(HiddenCells.Omit));
    }

    // An actor's answer lands only where the actor may read it: a public best row would show everyone what seat 1
    // planned over.
    [Fact]
    public void AnActorsSearchRefusesAnOutputWiderThanTheActor() {
        var definition = MiniNegamaxWorld(1) is var mini ? mini with {
            SearchRaw = new WorldSearchSection(Jobs: [mini.Search.Jobs![0] with { Actor = Principal.Seat(0) }]),
        } : null!;

        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var reason));
        Assert.Contains("SearchWidensAudience", reason);
        Assert.Contains("best", reason);

        definition = definition.WithWorldState(definition.AuthoredState.Select(row => row.Name.Value == "best" ?
            row with { Visibility = new StateVisibility(Readers: ["seat1"]) } : row).ToArray());
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out reason), reason);
    }
}
