using Xunit;
using static Puck.World.Tests.SolitaireFixtures;

namespace Puck.World.Tests;

// Each game's lifecycle owns a test collection, so unrelated deals can run concurrently. Fixtures and shuffle
// streams remain private to each test; no live server or mutable position is shared between collections.
public sealed class KlondikeDealLawTests {
    [Theory]
    [InlineData(1)] [InlineData(3)]
    public void NewDealPreservesCardsAndExposure(int draw) => SolitaireDealChecks.NewDeal("solitaireKlondike", draw, 24, 7);
    [Fact]
    public void RedealRecoversCardsAndAdvancesTheSavedStream() => SolitaireDealChecks.Redeal("solitaireKlondike");
}
public sealed class SpiderDealLawTests {
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void NewDealPreservesCardsAndExposure(int suits) => SolitaireDealChecks.NewDeal("solitaireSpider", suits, 50, 10);
    [Fact]
    public void RedealRecoversCardsAndAdvancesTheSavedStream() => SolitaireDealChecks.Redeal("solitaireSpider");
}
public sealed class FreeCellDealLawTests {
    [Fact]
    public void NewDealPreservesCardsAndExposure() => SolitaireDealChecks.NewDeal("solitaireFreecell", 1, 0, 52);
    [Fact]
    public void RedealRecoversCardsAndAdvancesTheSavedStream() => SolitaireDealChecks.Redeal("solitaireFreecell");
}
internal static class SolitaireDealChecks {
    public static void NewDeal(string game, int option, int stock, int faces) {
        var definition = Game(game, source => { Set(source, game, "option", option); Set(source, game, "request", 1); Set(source, game, "action", 1); });
        using var f = Fixtures.FreshServer(definition);
        Settle(f, game);
        Assert.Equal(1, Value(f, game, "result"));
        Assert.Equal(1, Value(f, game, "status"));
        Assert.Equal(stock, Count(f, game, 0));
        Assert.Equal(faces, Row(f, game + "Face").Cells!.Count(c => c.Value == 1));
        var tokens = f.Server.Definition.State.Where(r => r.Name.Value.StartsWith(game + "Pile", StringComparison.Ordinal)).SelectMany(r => r.Cells ?? []).Select(c => c.Key.Value).ToArray();
        Assert.Equal(game == "solitaireSpider" ? 104 : 52, tokens.Length);
        Assert.Equal(tokens.Length, tokens.Distinct().Count());
        var columns = game == "solitaireSpider" ? 10 : game == "solitaireFreecell" ? 8 : 7;
        for (var i = 0; i < columns; i++) { Assert.Equal(game == "solitaireSpider" ? (i < 4 ? 6 : 5) : game == "solitaireFreecell" ? (i < 4 ? 7 : 6) : i + 1, Count(f, game, i + 2)); }
        if (game == "solitaireSpider") { Assert.Equal(option, Row(f, game + "Suit").Cells!.Select(c => c.Value).Distinct().Count()); }
    }

    public static void Redeal(string game) {
        using var f = Fixtures.FreshServer(Game(game));
        Request(f, game, 1);
        if (game != "solitaireFreecell") { Request(f, game, 3); }
        var cursor = Row(f, game + "Stream").DrawCursor;
        Request(f, game, 1);
        Assert.Equal(2, Value(f, game, "deals"));
        Assert.Equal(0, Value(f, game, "moves"));
        Assert.True(Row(f, game + "Stream").DrawCursor > cursor);
        var tokens = f.Server.Definition.State.Where(r => r.Name.Value.StartsWith(game + "Pile", StringComparison.Ordinal)).SelectMany(r => r.Cells ?? []).Select(c => c.Key.Value).ToArray();
        Assert.Equal(game == "solitaireSpider" ? 104 : 52, tokens.Length);
        Assert.Equal(tokens.Length, tokens.Distinct().Count());
    }

}
