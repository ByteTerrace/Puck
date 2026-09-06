using System.Text.Json.Nodes;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Fresh authored card positions and condition-based stepping shared by the independent game suites.</summary>
internal static class SolitaireFixtures {
    public static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) { directory = directory.Parent; }
            return directory!.FullName;
        }
    }
    public static WorldDefinition Game(string game, Action<JsonObject>? edit = null) {
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
    public static JsonObject Control(JsonObject source, string game) => source["state"]!["world"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == game)!.AsObject();
    public static void Set(JsonObject source, string game, string key, long value) =>
        Control(source, game)["cells"]!.AsArray().Single(c => c!["key"]!.GetValue<string>() == key)!["value"] = value;
    public static WorldStateRow Row(WorldFixture f, string name) => f.Server.Definition.State.Single(r => r.Name.Value == name);
    public static long Value(WorldFixture f, string game, string key) => Row(f, game).Cells!.Single(c => c.Key.Value == key).Value;
    public static int Count(WorldFixture f, string game, int pile) => Row(f, $"{game}Pile{pile}").Cells?.Count ?? 0;
    public static void Steps(WorldFixture f, int n) { for (var i = 0; i < n; i++) { f.Step(); } }
    public static void Request(WorldFixture f, string game, int action, int from = -1, int to = -1, int card = -1) {
        foreach (var (key, value) in new (string, long)[] { ("action", action), ("from", from), ("to", to), ("card", card), ("request", Value(f, game, "request") + 1) }) {
            f.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: game, Value: value, Key: key, Kind: WorldDocumentWriteKind.Set));
        }
        Settle(f, game);
    }
    public static void Settle(WorldFixture f) => Settle(f, f.Server.Definition.State.Single(row => row.Name.Value is "solitaireKlondike" or "solitaireSpider" or "solitaireFreecell").Name.Value);
    public static void Settle(WorldFixture f, string game) {
        for (var i = 0; i < 240; i++) {
            f.Step();
            if (Value(f, game, "request") == Value(f, game, "applied") && Value(f, game, "stage") == 0 && Value(f, game, "busy") == 0) { return; }
        }
        Assert.Fail($"{game} did not settle within 240 ticks");
    }
    public static WorldDefinition Position(string game, Dictionary<int, int[]> piles, int[]? hidden = null, int action = 2, int from = 2, int to = 3, int card = 0) => Game(game, source => {
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

}
