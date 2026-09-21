using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: moving shipped game identity state into typed pools preserves the outcomes of
/// their committed gameplay sequences independently of the byte-for-byte export baselines.</summary>
public sealed class ShippedPoolMigrationBehaviorTests {
    private static long PoolField(JsonObject export, string poolName, int slot, string fieldName) {
        var pools = export["state"]!["pools"]!.AsArray();
        var pool = pools.Select(selector: static node => node!.AsObject()).Single(predicate: candidate => (candidate["name"]!.GetValue<string>() == poolName));
        var live = pool["snapshot"]!["live"]!.AsArray();
        var seed = live.Select(selector: static node => node!.AsObject()).Single(predicate: candidate => (candidate["slot"]!.GetValue<int>() == slot));
        var values = seed["values"]!.AsArray();
        var field = values.Select(selector: static node => node!.AsObject()).Single(predicate: candidate => (candidate["field"]!.GetValue<string>() == fieldName));

        return field["value"]!["value"]!.GetValue<long>();
    }
    private static long WorldValue(JsonObject export, string rowName) {
        var rows = export["state"]!["world"]!.AsArray();
        var row = rows.Select(selector: static node => node!.AsObject()).Single(predicate: candidate => (candidate["name"]!.GetValue<string>() == rowName));

        return row["value"]!.GetValue<long>();
    }

    [Fact]
    public void ArenaSequencePreservesCombatOutcomeAcrossFighterPoolMigration() {
        var run = ShippedWorldStateBaselines.Run(name: "arena");
        var export = JsonNode.Parse(utf8Json: run.Export)!.AsObject();

        var actual = new long[] {
                WorldValue(export: export, rowName: "arenaEnter"),
                WorldValue(export: export, rowName: "arenaJoined"),
                PoolField(export: export, fieldName: "body", poolName: "arenaFighters", slot: 0),
                PoolField(export: export, fieldName: "alive", poolName: "arenaFighters", slot: 0),
                PoolField(export: export, fieldName: "frags", poolName: "arenaFighters", slot: 0),
                PoolField(export: export, fieldName: "deaths", poolName: "arenaFighters", slot: 0),
                PoolField(export: export, fieldName: "health", poolName: "arenaFighters", slot: 0),
                PoolField(export: export, fieldName: "respawn", poolName: "arenaFighters", slot: 0),
                PoolField(export: export, fieldName: "taken", poolName: "arenaFighters", slot: 0),
                PoolField(export: export, fieldName: "body", poolName: "arenaFighters", slot: 1),
                PoolField(export: export, fieldName: "alive", poolName: "arenaFighters", slot: 1),
                PoolField(export: export, fieldName: "frags", poolName: "arenaFighters", slot: 1),
                PoolField(export: export, fieldName: "deaths", poolName: "arenaFighters", slot: 1),
                PoolField(export: export, fieldName: "health", poolName: "arenaFighters", slot: 1),
                PoolField(export: export, fieldName: "respawn", poolName: "arenaFighters", slot: 1),
                PoolField(export: export, fieldName: "taken", poolName: "arenaFighters", slot: 1),
                WorldValue(export: export, rowName: "arenaWinner"),
        };

        Assert.Equal(actual: actual, expected: [1L, 1L, 1L, 1L, 2L, 1L, 40L, 129L, 1L, 0L, 0L, 1L, 2L, 0L, 183L, 0L, 1L]);
    }
    [Fact]
    public void PongSequencePreservesScoringAndRallyOutcomeAcrossPaddlePoolMigration() {
        var export = JsonNode.Parse(utf8Json: ShippedWorldStateBaselines.Run(name: "pong").Export)!.AsObject();

        Assert.Equal(
            expected: [1L, 1L, 11L, 1L, 2L, 10L, 1L],
            actual: [
                PoolField(export: export, fieldName: "hits", poolName: "pongPaddles", slot: 0),
                PoolField(export: export, fieldName: "rally", poolName: "pongPaddles", slot: 0),
                PoolField(export: export, fieldName: "score", poolName: "pongPaddles", slot: 0),
                PoolField(export: export, fieldName: "hits", poolName: "pongPaddles", slot: 1),
                PoolField(export: export, fieldName: "rally", poolName: "pongPaddles", slot: 1),
                PoolField(export: export, fieldName: "score", poolName: "pongPaddles", slot: 1),
                WorldValue(export: export, rowName: "pongWinner"),
            ]
        );
        Assert.Equal(1L, PoolField(export: export, fieldName: "body", poolName: "pongBalls", slot: 0));
        Assert.Equal(1L, PoolField(export: export, fieldName: "body", poolName: "pongPaddles", slot: 0));
        Assert.Equal(2L, PoolField(export: export, fieldName: "body", poolName: "pongPaddles", slot: 1));
    }
}
