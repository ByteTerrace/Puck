using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: a <see cref="StateCellClock"/> has one wire form wherever it travels — a state row's
/// cell, a pool value and a disclosed observation spell it alike, with the follower's position and velocity as
/// decimal fixed-point strings and every zero member left out — and that form reads back to the same clock.</summary>
public sealed class StateCellClockWireLawTests {
    private static readonly StateCellClock Clock = new(
        EpochEngineTick: 257040L,
        EpochTick: 153L,
        V0: FixedQ4816.FromDouble(value: -0.5d).Value,
        Y0: FixedQ4816.FromDouble(value: 1.25d).Value
    );

    private static JsonNode? ClockOf(JsonNode? node) => node?["clock"];

    [Fact]
    public void A_row_cell_a_pool_value_and_an_observation_spell_a_clock_alike() {
        var row = new WorldStateRow(
            Cells: [new StateCell(Clock: Clock, Dynamics: new StateDynamics(Row: "chase"), Key: CellName.Parse(candidate: "0"), Value: CellValue.Fixed(rawBits: 0L))],
            Kind: CellKind.Fixed,
            Name: CellName.Parse(candidate: "follow")
        );
        var rowClock = ClockOf(node: JsonNode.Parse(json: JsonSerializer.Serialize(jsonTypeInfo: WorldJsonContext.Default.WorldStateRow, value: row))!["cells"]![0]);
        var poolClock = ClockOf(node: JsonSerializer.SerializeToNode(
            options: WorldJsonContext.Default.Options,
            value: new StatePoolValue(Clock: Clock, Field: CellName.Parse(candidate: "health"), Value: CellValue.Int(value: 40L))
        ));
        var observedClock = ClockOf(node: JsonSerializer.SerializeToNode(
            jsonTypeInfo: WorldJsonContext.Default.WorldObservedRowArray,
            value: [new WorldObservedRow(Cells: [new WorldObservedCell(Clock: Clock, Dynamics: new StateDynamics(Row: "chase"), Key: "0", Value: 0L)], Kind: CellKind.Fixed, Name: "follow")]
        )![0]!["cells"]![0]);
        var expected = JsonNode.Parse(json: """{"epochTick":153,"epochEngineTick":257040,"y0":"1.25","v0":"-0.5"}""");

        Assert.True(condition: JsonNode.DeepEquals(node1: expected, node2: rowClock), userMessage: rowClock?.ToJsonString());
        Assert.True(condition: JsonNode.DeepEquals(node1: expected, node2: poolClock), userMessage: poolClock?.ToJsonString());
        Assert.True(condition: JsonNode.DeepEquals(node1: expected, node2: observedClock), userMessage: observedClock?.ToJsonString());
        Assert.Equal(expected: Clock, actual: JsonSerializer.Deserialize(json: expected!.ToJsonString(), jsonTypeInfo: ((System.Text.Json.Serialization.Metadata.JsonTypeInfo<StateCellClock>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(StateCellClock)))));
    }
    [Fact]
    public void The_raw_form_is_refused_by_name() {
        var info = ((System.Text.Json.Serialization.Metadata.JsonTypeInfo<StateCellClock>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(StateCellClock)));

        var refused = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(json: """{"epochTick":153,"y0":81920}""", jsonTypeInfo: info));

        Assert.Contains(actualString: refused.Message, expectedSubstring: "clock.y0 must be a decimal string");
        _ = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(json: """{"epochTick":153,"spare":0}""", jsonTypeInfo: info));
    }
}
