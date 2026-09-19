using System.Text.Json.Nodes;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Fresh authored card positions and condition-based stepping shared by the independent game suites.</summary>
internal static class SolitaireFixtures {
    public static string Root {
        get {
            var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

            while (
                (directory is not null) &&
                !File.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: "Puck.slnx"
            ))
            ) { directory = directory.Parent; }
            return directory!.FullName;
        }
    }

    public static JsonObject Control(JsonObject source, string game) => source["state"]!["world"]!.AsArray().Single(predicate: r => (r!["name"]!.GetValue<string>() == game))!.AsObject();
    public static int Count(WorldFixture f, string game, int pile) => (Row(
        f: f,
        name: $"{game}Pile{pile}"
    ).Cells?.Count ?? 0);
    public static WorldDefinition Game(string game, Action<JsonObject>? edit = null) {
        var source = JsonNode.Parse(File.ReadAllText(path: Path.Combine(
            path1: Root,
            path2: $"src/Puck.World/Assets/worlds/games/{game[9..].ToLowerInvariant()}.world.json"
        )))!.AsObject();

        source["state"]!["world"]!.AsArray().Add(value: new JsonObject {
            ["name"] = "solitaire",
            ["kind"] = "int",
            ["capacity"] = 1,
            ["cells"] = new JsonArray(new JsonObject { ["key"] = "table", ["value"] = ((game == "solitaireKlondike")
            ? 1
            : ((game == "solitaireSpider")
                ? 2
                : 3)) }),
        });
        edit?.Invoke(source);
        var host = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()))!.AsObject();

        foreach (var field in new[] { "state", "rules", "patterns" }) { host[field] = source[field]!.DeepClone(); }
        return WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: host.ToJsonString()));
    }
    public static WorldDefinition Position(string game, Dictionary<int, int[]> piles, int[]? hidden = null, int action = 2, int from = 2, int to = 3, int card = 0) => Game(
        game,
        source => {
        var rows = source["state"]!["world"]!.AsArray();
        var used = piles.Values.SelectMany(selector: v => v).ToHashSet();
        var n = ((game == "solitaireSpider")
            ? 104
            : 52
        );

        foreach (var node in rows) {
            var row = node!.AsObject();
            var name = row["name"]!.GetValue<string>();

            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: (game + "Pile")
            )) {
                var pile = int.Parse(s: name[(game.Length + 4)..]);
                var values = (piles.TryGetValue(
                    key: pile,
                    value: out var found
                )
                    ? found
                    : ((pile == 0)
                        ? Enumerable.Range(
                            count: n,
                            start: 0
                        ).Where(predicate: c => !used.Contains(item: c)).ToArray()
                        : []
                ));

                row["cells"] = new JsonArray(values.Select(selector: c => ((JsonNode)new JsonObject { ["key"] = c.ToString(), ["value"] = true })).ToArray());
            }
            if (name == (game + "Face")) { foreach (var cell in row["cells"]!.AsArray()) { cell!["value"] = ((hidden?.Contains(value: int.Parse(s: cell["key"]!.GetValue<string>())) == true)
                ? 0
                : 1
            ); } }
        }
        Set(
            game: game,
            key: "status",
            source: source,
            value: 1
        ); Set(
            game: game,
            key: "busy",
            source: source,
            value: 1
        ); Set(
            game: game,
            key: "request",
            source: source,
            value: 1
        ); Set(
            game: game,
            key: "action",
            source: source,
            value: action
        );
        Set(
            game: game,
            key: "from",
            source: source,
            value: from
        ); Set(
            game: game,
            key: "to",
            source: source,
            value: to
        ); Set(
            game: game,
            key: "card",
            source: source,
            value: card
        );
    }
    );
    public static void Request(WorldFixture f, string game, int action, int from = -1, int to = -1, int card = -1) {
        foreach (var (key, value) in new (string, long)[] { ("action", action), ("from", from), ("to", to), ("card", card), ("request", (Value(
            f: f,
            game: game,
            key: "request"
        ) + 1)) }) {
            f.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.Console,
                Row: game,
                Value: value,
                Key: key,
                Kind: WorldDocumentWriteKind.Set
            ));
        }
        Settle(
            f: f,
            game: game
        );
    }
    public static WorldStateRow Row(WorldFixture f, string name) => f.Server.Definition.State.Single(predicate: r => (r.Name.Value == name));
    public static void Set(JsonObject source, string game, string key, long value) =>
        Control(
            game: game,
            source: source
        )["cells"]!.AsArray().Single(predicate: c => (c!["key"]!.GetValue<string>() == key))!["value"] = value;
    public static void Settle(WorldFixture f) => Settle(
        f: f,
        game: f.Server.Definition.State.Single(predicate: row => (row.Name.Value is "solitaireKlondike" or "solitaireSpider" or "solitaireFreecell")).Name.Value
    );
    public static void Settle(WorldFixture f, string game) {
        for (var i = 0; (i < 240); i++) {
            f.Step();
            if (
                (Value(
                f: f,
                game: game,
                key: "request"
            ) == Value(
                f: f,
                game: game,
                key: "applied"
            )) &&
                (Value(
                f: f,
                game: game,
                key: "stage"
            ) == 0) &&
                (Value(
                f: f,
                game: game,
                key: "busy"
            ) == 0)
            ) { return; }
        }
        Assert.Fail(message: $"{game} did not settle within 240 ticks");
    }
    public static void Steps(WorldFixture f, int n) { for (var i = 0; (i < n); i++) { f.Step(); } }
    public static long Value(WorldFixture f, string game, string key) => Row(
        f: f,
        name: game
    ).Cells!.Single(predicate: c => (c.Key.Value == key)).Value.Raw;

}
