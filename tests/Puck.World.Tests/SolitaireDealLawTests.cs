using Xunit;
using static Puck.World.Tests.SolitaireFixtures;

namespace Puck.World.Tests;

// Each game's lifecycle owns a test collection, so unrelated deals can run concurrently. Fixtures and shuffle
// streams remain private to each test; no live server or mutable position is shared between collections.
public sealed class KlondikeDealLawTests {
    [InlineData(1)]
    [InlineData(3)]
    [Theory]
    public void NewDealPreservesCardsAndExposure(int draw) => SolitaireDealChecks.NewDeal(
        faces: 7,
        game: "solitaireKlondike",
        option: draw,
        stock: 24
    );
    [Fact]
    public void RedealRecoversCardsAndAdvancesTheSavedStream() => SolitaireDealChecks.Redeal(game: "solitaireKlondike");
}
public sealed class SpiderDealLawTests {
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [Theory]
    public void NewDealPreservesCardsAndExposure(int suits) => SolitaireDealChecks.NewDeal(
        faces: 10,
        game: "solitaireSpider",
        option: suits,
        stock: 50
    );
    [Fact]
    public void RedealRecoversCardsAndAdvancesTheSavedStream() => SolitaireDealChecks.Redeal(game: "solitaireSpider");
}
public sealed class FreeCellDealLawTests {
    [Fact]
    public void NewDealPreservesCardsAndExposure() => SolitaireDealChecks.NewDeal(
        faces: 52,
        game: "solitaireFreecell",
        option: 1,
        stock: 0
    );
    [Fact]
    public void RedealRecoversCardsAndAdvancesTheSavedStream() => SolitaireDealChecks.Redeal(game: "solitaireFreecell");
}

internal static class SolitaireDealChecks {
    public static void NewDeal(string game, int option, int stock, int faces) {
        var definition = Game(
            game,
            source => { Set(
            game: game,
            key: "option",
            source: source,
            value: option
        ); Set(
            game: game,
            key: "request",
            source: source,
            value: 1
        ); Set(
            game: game,
            key: "action",
            source: source,
            value: 1
        ); }
        );
        using var f = Fixtures.FreshServer(definition);

        Settle(
            f: f,
            game: game
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
            1,
            Value(
                f: f,
                game: game,
                key: "status"
            )
        );
        Assert.Equal(
            stock,
            Count(
                f: f,
                game: game,
                pile: 0
            )
        );
        Assert.Equal(
            faces,
            Row(
                f: f,
                name: (game + "Face")
            ).Cells!.Count(predicate: c => (c.Value.AsInt == 1))
        );
        var tokens = f.Server.Definition.State.Where(predicate: r => r.Name.Value.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: (game + "Pile")
        )).SelectMany(selector: r => (r.Cells ?? [])).Select(selector: c => c.Key.Value).ToArray();

        Assert.Equal(
            ((game == "solitaireSpider")
            ? 104
            : 52),
            tokens.Length
        );
        Assert.Equal(
            tokens.Length,
            tokens.Distinct().Count()
        );
        var columns = ((game == "solitaireSpider")
            ? 10
            : ((game == "solitaireFreecell")
                ? 8
                : 7
        ));

        for (var i = 0; (i < columns); i++) { Assert.Equal(
            ((game == "solitaireSpider")
            ? ((i < 4)
                ? 6
                : 5)
            : ((game == "solitaireFreecell")
                ? ((i < 4)
                    ? 7
                    : 6)
                : (i + 1))),
            Count(
                f: f,
                game: game,
                pile: (i + 2)
            )
        ); }
        if (game == "solitaireSpider") { Assert.Equal(
            option,
            Row(
                f: f,
                name: (game + "Suit")
            ).Cells!.Select(selector: c => c.Value).Distinct().Count()
        ); }
    }
    public static void Redeal(string game) {
        using var f = Fixtures.FreshServer(Game(game));

        Request(
            f,
            game,
            1
        );
        if (game != "solitaireFreecell") { Request(
            f,
            game,
            3
        ); }
        var cursor = Row(
            f: f,
            name: (game + "Stream")
        ).DrawCursor;

        Request(
            f,
            game,
            1
        );
        Assert.Equal(
            2,
            Value(
                f: f,
                game: game,
                key: "deals"
            )
        );
        Assert.Equal(
            0,
            Value(
                f: f,
                game: game,
                key: "moves"
            )
        );
        Assert.True(condition: (Row(
            f: f,
            name: (game + "Stream")
        ).DrawCursor > cursor));
        var tokens = f.Server.Definition.State.Where(predicate: r => r.Name.Value.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: (game + "Pile")
        )).SelectMany(selector: r => (r.Cells ?? [])).Select(selector: c => c.Key.Value).ToArray();

        Assert.Equal(
            ((game == "solitaireSpider")
            ? 104
            : 52),
            tokens.Length
        );
        Assert.Equal(
            tokens.Length,
            tokens.Distinct().Count()
        );
    }

}
