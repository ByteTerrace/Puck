using System.Text.Json.Nodes;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The shipped patience programs run through the ordinary rule and mutation pipeline.</summary>
public sealed class SolitaireLawTests {
    [Fact]
    public void AllSolitaireModulesComposeWithTheShippedNexus() {
        var path = Path.Combine(Root, "src/Puck.World/Assets/worlds/puck.world.json");
        Assert.True(WorldDefinitionFileSource.TryLoad(path, out var definition, out _, out var reason), reason);
        Assert.Contains(definition!.State, row => row.Name.Value == "solitaireFreecell");
        Assert.Contains(definition.State, row => row.Name.Value == "solitaireSpider");
        Assert.Contains(definition.State, row => row.Name.Value == "solitaireKlondike");
        Assert.DoesNotContain(definition.State.Single(row => row.Name.Value == "spider").Cells!, cell => cell.Key.Value == "request");
    }
    private static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) { directory = directory.Parent; }
            return directory!.FullName;
        }
    }
    private static WorldDefinition Game(string game, Action<JsonObject>? edit = null) {
        var source = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, $"src/Puck.World/Assets/worlds/games/{game[9..].ToLowerInvariant()}.world.json")))!.AsObject();
        source["state"]!["world"]!.AsArray().Add(new JsonObject {
            ["name"] = "solitaire", ["kind"] = "int", ["capacity"] = 1,
            ["cells"] = new JsonArray(new JsonObject { ["key"] = "table", ["value"] = game == "solitaireKlondike" ? 1 : game == "solitaireSpider" ? 2 : 3 })
        });
        edit?.Invoke(source);
        var host = JsonNode.Parse(WorldDefinitionSerialization.Serialize(Fixtures.BuildDocument()))!.AsObject();
        foreach (var field in new[] { "state", "rules", "patterns" }) { host[field] = source[field]!.DeepClone(); }
        return WorldDefinitionSerialization.Deserialize(System.Text.Encoding.UTF8.GetBytes(host.ToJsonString()));
    }
    private static JsonObject Control(JsonObject source, string game) => source["state"]!["world"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == game)!.AsObject();
    private static void Set(JsonObject source, string game, string key, long value) =>
        Control(source, game)["cells"]!.AsArray().Single(c => c!["key"]!.GetValue<string>() == key)!["value"] = value;
    private static WorldStateRow Row(WorldFixture f, string name) => f.Server.Definition.State.Single(r => r.Name.Value == name);
    private static long Value(WorldFixture f, string game, string key) => Row(f, game).Cells!.Single(c => c.Key.Value == key).Value;
    private static int Count(WorldFixture f, string game, int pile) => Row(f, $"{game}Pile{pile}").Cells?.Count ?? 0;
    private static void Steps(WorldFixture f, int n = 48) { for (var i = 0; i < n; i++) { f.Step(); } }
    private static void Request(WorldFixture f, string game, int action, int from = -1, int to = -1, int card = -1) {
        foreach (var (key, value) in new (string, long)[] { ("action", action), ("from", from), ("to", to), ("card", card), ("request", Value(f, game, "request") + 1) }) {
            f.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: game, Value: value, Key: key, Kind: WorldDocumentWriteKind.Set));
        }
        Settle(f, game);
    }
    private static void Settle(WorldFixture f, string game) {
        for (var i = 0; i < 240; i++) {
            f.Step();
            if (Value(f, game, "request") == Value(f, game, "applied") && Value(f, game, "stage") == 0 && Value(f, game, "busy") == 0) { return; }
        }
        Assert.Fail($"{game} did not settle within 240 ticks");
    }
    private static WorldDefinition Position(string game, Dictionary<int, int[]> piles, int[]? hidden = null, int action = 2, int from = 2, int to = 3, int card = 0) => Game(game, source => {
        var rows = source["state"]!["world"]!.AsArray();
        var used = piles.Values.SelectMany(v => v).ToHashSet();
        var n = game == "solitaireSpider" ? 104 : 52;
        foreach (var node in rows) {
            var row = node!.AsObject();
            var name = row["name"]!.GetValue<string>();
            if (name.StartsWith(game + "Pile", StringComparison.Ordinal)) {
                var pile = int.Parse(name[(game.Length + 4)..]);
                var values = piles.TryGetValue(pile, out var found) ? found : pile == 0 ? Enumerable.Range(0, n).Where(c => !used.Contains(c)).ToArray() : [];
                row["cells"] = new JsonArray(values.Select(c => (JsonNode)new JsonObject { ["key"] = c.ToString(), ["value"] = true }).ToArray());
            }
            if (name == game + "Face") { foreach (var cell in row["cells"]!.AsArray()) { cell!["value"] = hidden?.Contains(int.Parse(cell["key"]!.GetValue<string>())) == true ? 0 : 1; } }
        }
        Set(source, game, "status", 1); Set(source, game, "busy", 1); Set(source, game, "request", 1); Set(source, game, "action", action);
        Set(source, game, "from", from); Set(source, game, "to", to); Set(source, game, "card", card);
    });

    [Theory]
    [InlineData("solitaireKlondike", 1, 24, 7)]
    [InlineData("solitaireKlondike", 3, 24, 7)]
    [InlineData("solitaireSpider", 1, 50, 10)]
    [InlineData("solitaireSpider", 2, 50, 10)]
    [InlineData("solitaireSpider", 4, 50, 10)]
    [InlineData("solitaireFreecell", 1, 0, 52)]
    public void NewDealsPreserveEveryCardAndExposeOnlyTheProperCards(string game, int option, int stock, int faces) {
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

    [Theory]
    [InlineData(3, 17, true)] // black 4 onto red 5
    [InlineData(3, 4, false)] // same colour
    [InlineData(3, 18, false)] // wrong rank
    public void KlondikeJudgesRankAndColourBeforeTransferring(int card, int target, bool legal) {
        using var f = Fixtures.FreshServer(Position("solitaireKlondike", new() { [2] = [card], [3] = [target] }, card: card));
        Steps(f);
        Assert.Equal(legal ? 1 : -1, Value(f, "solitaireKlondike", "result"));
        Assert.Equal(legal ? 0 : 1, Count(f, "solitaireKlondike", 2));
        Assert.Equal(legal ? new[] { target.ToString(), card.ToString() } : [target.ToString()], Row(f, "solitaireKlondikePile3").Cells!.Select(c => c.Key.Value));
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(11, false)]
    public void OnlyKingsStartEmptyKlondikeColumns(int card, bool legal) {
        using var f = Fixtures.FreshServer(Position("solitaireKlondike", new() { [2] = [card] }, card: card));
        Steps(f);
        Assert.Equal(legal ? 1 : -1, Value(f, "solitaireKlondike", "result"));
    }

    [Fact]
    public void KlondikeMovesAnEntireAlternatingRunAndRevealsTheCoveredCard() {
        using var f = Fixtures.FreshServer(Position("solitaireKlondike", new() { [2] = [40, 4, 16, 2], [3] = [18] }, hidden: [40], card: 4));
        Steps(f);
        Assert.Equal(1, Value(f, "solitaireKlondike", "result"));
        Assert.Equal(new[] { "18", "4", "16", "2" }, Row(f, "solitaireKlondikePile3").Cells!.Select(c => c.Key.Value));
        Assert.Equal(1, Row(f, "solitaireKlondikeFace").Cells!.Single(c => c.Key.Value == "40").Value);
    }

    [Fact]
    public void AHiddenOrBrokenRunCannotMove() {
        foreach (var hidden in new[] { true, false }) {
            using var f = Fixtures.FreshServer(Position("solitaireKlondike", new() { [2] = hidden ? [4, 16] : [4, 3], [3] = [18] }, hidden: hidden ? [4] : [], card: 4));
            Steps(f);
            Assert.Equal(-1, Value(f, "solitaireKlondike", "result"));
            Assert.Equal(2, Count(f, "solitaireKlondike", 2));
        }
    }

    [Theory]
    [InlineData("solitaireKlondike")]
    [InlineData("solitaireFreecell")]
    public void FoundationsRequireSuitAndAscendingRank(string game) {
        using var f = Fixtures.FreshServer(Position(game, new() { [2] = [0], [3] = [1], [4] = [13] }, to: 10));
        Steps(f);
        Assert.Equal(1, Value(f, game, "result"));
        Request(f, game, 2, 4, 10, 13);
        Assert.Equal(-1, Value(f, game, "result"));
        Request(f, game, 2, 3, 10, 1);
        Assert.Equal(1, Value(f, game, "result"));
        Assert.Equal(new[] { "0", "1" }, Row(f, game + "Pile10").Cells!.Select(c => c.Key.Value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void StockPassesPreserveOrderAndTheFinalShortDraw(int amount) {
        var definition = Position("solitaireKlondike", new() { [0] = [0, 1, 2, 3, 4] }, action: 0);
        definition = definition with { StateRaw = definition.StateRaw! with { World = [.. definition.State.Select(r => r.Name.Value == "solitaireKlondike" ? r with { Cells = [.. r.Cells!.Select(c => c.Key.Value == "activeOption" ? c with { Value = amount } : c)] } : r)] } };
        using var f = Fixtures.FreshServer(definition);
        Steps(f);
        Request(f, "solitaireKlondike", 3);
        Assert.Equal(amount, Count(f, "solitaireKlondike", 1));
        while (Count(f, "solitaireKlondike", 0) > 0) { Request(f, "solitaireKlondike", 3); }
        Assert.Equal(new[] { "0", "1", "2", "3", "4" }, Row(f, "solitaireKlondikePile1").Cells!.Select(c => c.Key.Value));
        Request(f, "solitaireKlondike", 3);
        Assert.Equal(new[] { "0", "1", "2", "3", "4" }, Row(f, "solitaireKlondikePile0").Cells!.Select(c => c.Key.Value));
        Assert.Equal(1, Value(f, "solitaireKlondike", "passes"));
    }

    [Fact]
    public void SpiderMayBuildAcrossSuitsButOnlyMovesSameSuitRuns() {
        using var f = Fixtures.FreshServer(Position("solitaireSpider", new() { [2] = [4], [3] = [18] }, card: 4));
        Steps(f);
        Assert.Equal(1, Value(f, "solitaireSpider", "result"));
        Request(f, "solitaireSpider", 2, 3, 4, 18);
        Assert.Equal(-1, Value(f, "solitaireSpider", "result"));
        Request(f, "solitaireSpider", 2, 3, 4, 4);
        Assert.Equal(1, Value(f, "solitaireSpider", "result"));
    }

    [Fact]
    public void SpiderClearsCompleteSuitRunsAndCountsTheWin() {
        var piles = new Dictionary<int, int[]> { [2] = Enumerable.Range(0, 13).Reverse().ToArray(), [12] = Enumerable.Range(13, 91).ToArray() };
        using var f = Fixtures.FreshServer(Position("solitaireSpider", piles, action: 0));
        Steps(f);
        Assert.Equal(104, Count(f, "solitaireSpider", 12));
        Assert.Equal(2, Value(f, "solitaireSpider", "status"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpiderRequiresEveryColumnToBeOccupiedBeforeDealing(bool allOccupied) {
        var piles = Enumerable.Range(2, allOccupied ? 10 : 9).ToDictionary(z => z, z => new[] { z - 2 });
        using var f = Fixtures.FreshServer(Position("solitaireSpider", piles, action: 3));
        var before = Count(f, "solitaireSpider", 0);
        Steps(f);
        Assert.Equal(allOccupied ? 1 : -1, Value(f, "solitaireSpider", "result"));
        Assert.Equal(before - (allOccupied ? 10 : 0), Count(f, "solitaireSpider", 0));
    }

    [Fact]
    public void FreeCellsHoldOneCardAndFoundationMovesCannotBeUndoneAsTableauMoves() {
        using var f = Fixtures.FreshServer(Position("solitaireFreecell", new() { [2] = [0], [3] = [1] }, to: 14));
        Steps(f);
        Assert.Equal(1, Value(f, "solitaireFreecell", "result"));
        Request(f, "solitaireFreecell", 2, 3, 14, 1);
        Assert.Equal(-1, Value(f, "solitaireFreecell", "result"));
        Request(f, "solitaireFreecell", 2, 14, 10, 0);
        Assert.Equal(1, Value(f, "solitaireFreecell", "result"));
        Request(f, "solitaireFreecell", 2, 10, 4, 0);
        Assert.Equal(-1, Value(f, "solitaireFreecell", "result"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void FreeCellSupermovesAreBoundedByAvailableWorkspace(bool spareColumn, bool legal) {
        var piles = new Dictionary<int, int[]> { [2] = [4, 16, 2], [3] = [18], [4] = [30], [5] = [31], [6] = [32], [7] = [33], [8] = [34], [14] = [40], [15] = [41] };
        if (!spareColumn) { piles[9] = [35]; }
        // One further card makes only one free cell available: capacity two, or four with a spare column.
        piles[16] = [42];
        using var f = Fixtures.FreshServer(Position("solitaireFreecell", piles, card: 4));
        Steps(f);
        Assert.Equal(legal ? 1 : -1, Value(f, "solitaireFreecell", "result"));
    }
    [Theory]
    [InlineData("solitaireKlondike")]
    [InlineData("solitaireSpider")]
    [InlineData("solitaireFreecell")]
    public void ASecondDealRecoversEveryCardAndAdvancesTheSavedShuffleStream(string game) {
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEmptyFreeCellDestinationIsNotAdditionalWorkspace(bool spare) {
        var piles = new Dictionary<int, int[]> { [2] = [4, 16, 2], [4] = [30], [5] = [31], [6] = [32], [7] = [33], [8] = [34], [14] = [40], [15] = [41], [16] = [42] };
        if (!spare) { piles[9] = [35]; }
        using var f = Fixtures.FreshServer(Position("solitaireFreecell", piles, card: 4));
        Settle(f, "solitaireFreecell");
        Assert.Equal(spare ? 1 : -1, Value(f, "solitaireFreecell", "result"));
    }

    [Theory]
    [InlineData("solitaireKlondike")]
    [InlineData("solitaireFreecell")]
    public void TheLastFoundationMoveWinsAndFurtherMovesAreRefused(string game) {
        var piles = new Dictionary<int, int[]> { [2] = [12], [10] = Enumerable.Range(0, 12).ToArray(), [11] = Enumerable.Range(13, 13).ToArray(), [12] = Enumerable.Range(26, 13).ToArray(), [13] = Enumerable.Range(39, 13).ToArray() };
        using var f = Fixtures.FreshServer(Position(game, piles, card: 12, to: 10));
        Settle(f, game);
        Assert.Equal(2, Value(f, game, "status"));
        Assert.Equal(1, Value(f, game, "moves"));
        Request(f, game, 2, 10, 2, 12);
        Assert.Equal(-1, Value(f, game, "result"));
        Assert.Equal(13, Count(f, game, 10));
    }

    [Theory]
    [InlineData(99, 3, 4)]
    [InlineData(2, 99, 4)]
    [InlineData(2, 2, 4)]
    [InlineData(2, 3, -1)]
    [InlineData(2, 3, 999)]
    public void InvalidMoveAddressesLeavePilesAndMoveCountUntouched(int from, int to, int card) {
        using var f = Fixtures.FreshServer(Position("solitaireKlondike", new() { [2] = [4], [3] = [18] }, from: from, to: to, card: card));
        Settle(f, "solitaireKlondike");
        Assert.Equal(-1, Value(f, "solitaireKlondike", "result"));
        Assert.Equal(0, Value(f, "solitaireKlondike", "moves"));
        Assert.Equal("4", Assert.Single(Row(f, "solitaireKlondikePile2").Cells!).Key.Value);
        Assert.Equal("18", Assert.Single(Row(f, "solitaireKlondikePile3").Cells!).Key.Value);
    }

    [Theory]
    [InlineData("solitaireKlondike", 2)]
    [InlineData("solitaireSpider", 3)]
    [InlineData("solitaireFreecell", 0)]
    public void UnsupportedNewDealOptionsRefuseWithoutConsumingRandomness(string game, int option) {
        using var f = Fixtures.FreshServer(Game(game, source => Set(source, game, "option", option)));
        Request(f, game, 1);
        Assert.Equal(-1, Value(f, game, "result"));
        Assert.Equal(0, Row(f, game + "Stream").DrawCursor);
        Assert.Equal(game == "solitaireSpider" ? 104 : 52, Count(f, game, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SpiderDoesNotClearHiddenOrMixedSuitKingToAceColumns(bool hidden) {
        var run = Enumerable.Range(0, 13).Reverse().ToArray();
        if (!hidden) { run[^1] = 13; }
        using var f = Fixtures.FreshServer(Position("solitaireSpider", new() { [2] = run }, hidden: hidden ? [5] : [], action: 0));
        Settle(f, "solitaireSpider");
        Assert.Equal(13, Count(f, "solitaireSpider", 2));
        Assert.Equal(0, Count(f, "solitaireSpider", 12));
    }

    [Fact]
    public void SwitchingAwayPausesADealAndChangingAnOptionDoesNotChangeAnActiveDeal() {
        using var f = Fixtures.FreshServer(Game("solitaireKlondike", source => {
            Set(source, "solitaireKlondike", "option", 3);
            Set(source, "solitaireKlondike", "action", 1);
            Set(source, "solitaireKlondike", "request", 1);
            Set(source, "solitaire", "table", 0);
        }));
        Steps(f, 10);
        Assert.Equal(0, Value(f, "solitaireKlondike", "applied"));
        f.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "solitaire", Value: 1, Key: "table", Kind: WorldDocumentWriteKind.Set));
        Settle(f, "solitaireKlondike");
        f.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "solitaireKlondike", Value: 1, Key: "option", Kind: WorldDocumentWriteKind.Set));
        Request(f, "solitaireKlondike", 3);
        Assert.Equal(3, Value(f, "solitaireKlondike", "activeOption"));
        Assert.Equal(3, Count(f, "solitaireKlondike", 1));
    }

    [Fact]
    public void SpiderLocksItsSuitCountWhenTheDealIsAccepted() {
        using var f = Fixtures.FreshServer(Game("solitaireSpider", source => {
            Set(source, "solitaireSpider", "option", 4);
            Set(source, "solitaireSpider", "action", 1);
            Set(source, "solitaireSpider", "request", 1);
        }));
        Steps(f, 2);
        f.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "solitaireSpider", Value: 1, Key: "option", Kind: WorldDocumentWriteKind.Set));
        Settle(f, "solitaireSpider");
        Assert.Equal(4, Value(f, "solitaireSpider", "activeOption"));
        Assert.Equal(4, Row(f, "solitaireSpiderSuit").Cells!.Select(c => c.Value).Distinct().Count());
        Assert.Equal(1, Value(f, "solitaireSpider", "option"));
    }

}
