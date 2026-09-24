using Xunit;

namespace Puck.World.Browser.Tests;

public sealed class BrowserVectorTests {
    private static StateVector SampleVector(int dimensions, int index = 0, sbyte component = 127) {
        var components = new sbyte[dimensions];

        components[index] = component;
        StateVector.TryCreate(components: components, error: out _, vector: out var vector);

        return vector!;
    }
    private static StateSpace SampleSpace(string name = "lore", int dimensions = 32) =>
        new(
            dimensions: dimensions,
            model: "text-embedding-3-small",
            name: CellName.Parse(candidate: name),
            revision: "1"
        );
    private static WorldDefinition BuildDefinition(
        WorldStateRow[] rows,
        StateSpace[]? spaces = null,
        WorldRule[]? rules = null
    ) =>
        new(
            Rules: rules,
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(
                Spaces: (spaces ?? []),
                World: rows
            )
        );

    [Fact]
    public void StateHash_CoversVectorBytes() {
        var space = SampleSpace(dimensions: 32);
        var v1 = SampleVector(dimensions: 32, index: 0);
        var v2 = SampleVector(dimensions: 32, index: 1);

        var row1 = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "k1"), Value: CellValue.Vector(components: v1.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "vRow"),
            Space: "lore"
        );
        var row2 = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "k1"), Value: CellValue.Vector(components: v2.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "vRow"),
            Space: "lore"
        );

        var def1 = BuildDefinition(rows: [row1], spaces: [space]);
        var def2 = BuildDefinition(rows: [row2], spaces: [space]);
        var def3 = BuildDefinition(rows: [row1], spaces: [space]);

        var session1 = new BrowserSession(definition: def1);
        var session2 = new BrowserSession(definition: def2);
        var session3 = new BrowserSession(definition: def3);

        var hash1 = session1.StateHash();
        var hash2 = session2.StateHash();
        var hash3 = session3.StateHash();

        Assert.NotEqual(actual: hash2, expected: hash1);
        Assert.Equal(actual: hash3, expected: hash1);
    }
    [Fact]
    public void Nearest_RanksIntoItsDestination() {
        var space = SampleSpace(dimensions: 32);
        var v1 = SampleVector(dimensions: 32, index: 0);

        var kbRow = new WorldStateRow(
            Capacity: 10,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "q1"), Value: CellValue.Vector(components: v1.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "kb"),
            Space: "lore"
        );
        var scoresRow = new WorldStateRow(
            Capacity: 10,
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "scores")
        );

        var rule = new WorldRule(
            Name: CellName.Parse(candidate: "run_nearest"),
            Effects: [
                new ActionEffect.TransformState(
                    Transform: new StateTransform.Nearest(
                        From: "kb",
                        Query: "kb[q1]",
                        Into: "scores",
                        K: 3
                    )
                )
            ]
        );

        var def = BuildDefinition(rows: [kbRow, scoresRow], rules: [rule], spaces: [space]);
        var session = new BrowserSession(definition: def);

        var result = session.Judge(tick: 1UL);

        Assert.Empty(collection: result.Refusals);
        Assert.Contains(
            collection: result.Writes,
            filter: write => string.Equals(
                a: write.Row,
                b: "scores",
                comparisonType: StringComparison.Ordinal
            )
        );
    }
    [Fact]
    public void Remember_WritesItsDestinationVector() {
        var space = SampleSpace(dimensions: 32);
        var v1 = SampleVector(dimensions: 32, index: 0);

        var memRow = new WorldStateRow(
            Capacity: 10,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "mem"),
            Space: "lore"
        );
        var queryRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Vector(components: v1.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "query"),
            Space: "lore"
        );

        var rule = new WorldRule(
            Name: CellName.Parse(candidate: "run_remember"),
            Effects: [
                new ActionEffect.TransformState(
                    Transform: new StateTransform.Remember(
                        From: "query",
                        Into: "mem",
                        Key: "k1",
                        UnlessWithin: "0.8"
                    )
                )
            ]
        );

        var def = BuildDefinition(rows: [memRow, queryRow], rules: [rule], spaces: [space]);
        var session = new BrowserSession(definition: def);

        var result = session.Judge(tick: 1UL);
        var read = session.ReadRow(
            key: "k1",
            row: "mem"
        );

        Assert.Empty(collection: result.Refusals);
        Assert.True(condition: read.Found);
        Assert.Equal(
            expected: nameof(CellKind.Vector),
            actual: read.Kind
        );
        Assert.NotNull(@object: read.Value);
    }
}
