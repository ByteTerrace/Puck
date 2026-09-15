using Xunit;

namespace Puck.World.Browser.Tests;

public sealed class BrowserVectorTests {
    private static StateVector SampleVector(int dimensions, int index = 0, sbyte component = 127) {
        var components = new sbyte[dimensions];
        components[index] = component;
        StateVector.TryCreate(components: components, vector: out var vector, error: out _);

        return vector!;
    }

    private static StateSpace SampleSpace(string name = "lore", int dimensions = 32) =>
        new(
            Dimensions: dimensions,
            Model: "text-embedding-3-small",
            Name: CellName.Parse(candidate: name),
            Revision: "1"
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
            Cells: [new StateCell(Key: CellName.Parse(candidate: "k1"), Vector: v1)],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "vRow"),
            Space: "lore"
        );
        var row2 = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "k1"), Vector: v2)],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "vRow"),
            Space: "lore"
        );

        var def1 = BuildDefinition(rows: [row1], spaces: [space]);
        var def2 = BuildDefinition(rows: [row2], spaces: [space]);
        var def3 = BuildDefinition(rows: [row1], spaces: [space]);

        var comp1 = WorldRuleCompilation.Compile(definition: def1);
        var comp2 = WorldRuleCompilation.Compile(definition: def2);
        var comp3 = WorldRuleCompilation.Compile(definition: def3);

        var session1 = new BrowserSession(definition: def1, compilation: comp1);
        var session2 = new BrowserSession(definition: def2, compilation: comp2);
        var session3 = new BrowserSession(definition: def3, compilation: comp3);

        var hash1 = session1.StateHash();
        var hash2 = session2.StateHash();
        var hash3 = session3.StateHash();

        Assert.NotEqual(hash1, hash2);
        Assert.Equal(hash1, hash3);
    }

    [Fact]
    public void Nearest_RefusedInBrowserSession() {
        var space = SampleSpace(dimensions: 32);
        var v1 = SampleVector(dimensions: 32, index: 0);

        var kbRow = new WorldStateRow(
            Capacity: 10,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "q1"), Vector: v1)],
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

        var def = BuildDefinition(rows: [kbRow, scoresRow], spaces: [space], rules: [rule]);
        var comp = WorldRuleCompilation.Compile(definition: def);
        var session = new BrowserSession(definition: def, compilation: comp);

        var result = session.Judge(tick: 1UL);

        Assert.NotEmpty(result.Refusals);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(RuleEffectRefusal.MutationRejected.ToString(), refusal.Category);
        Assert.Contains("a frame does not apply a Nearest vector transform", refusal.Detail);
    }

    [Fact]
    public void Remember_RefusedInBrowserSession() {
        var space = SampleSpace(dimensions: 32);
        var v1 = SampleVector(dimensions: 32, index: 0);

        var memRow = new WorldStateRow(
            Capacity: 10,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "mem"),
            Space: "lore"
        );
        var queryRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Vector: v1)],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "query"),
            Space: "lore"
        );

        var rule = new WorldRule(
            Name: CellName.Parse(candidate: "run_remember"),
            Effects: [
                new ActionEffect.TransformState(
                    Transform: new StateTransform.Remember(
                        Into: "mem",
                        Key: "k1",
                        From: "query",
                        UnlessWithin: "0.8"
                    )
                )
            ]
        );

        var def = BuildDefinition(rows: [memRow, queryRow], spaces: [space], rules: [rule]);
        var comp = WorldRuleCompilation.Compile(definition: def);
        var session = new BrowserSession(definition: def, compilation: comp);

        var result = session.Judge(tick: 1UL);

        Assert.NotEmpty(result.Refusals);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(RuleEffectRefusal.MutationRejected.ToString(), refusal.Category);
        Assert.Contains("a frame does not apply a Remember vector transform", refusal.Detail);
    }
}
