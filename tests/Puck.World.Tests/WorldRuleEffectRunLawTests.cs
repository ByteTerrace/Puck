using Xunit;

namespace Puck.World.Tests;

/// <summary>A contiguous run of state effects in one rule is one transaction: every effect evaluates its own
/// expression against the run's candidate, and a later effect refused by its destination's envelope drops the whole
/// run, earlier writes included, with the refusal reported by rule and effect.</summary>
public sealed class WorldRuleEffectRunLawTests {
    private static WorldStateRow Slot(string name, long value, long max) =>
        new(WorldCellName.Parse(name), CellKind.Int, Min: 0, Max: max, Cells: [new WorldStateCell(WorldStateRow.SlotKey, value)]);
    private static ActionEffect.SetState Set(string state, params WorldValueToken[] tokens) => new(State: state, Expression: new WorldValueExpression(tokens));
    private static WorldValueToken[] Tz(string row) => [new WorldValueToken.State(row), new WorldValueToken.TrailingZeroCount()];
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;

    private static WorldDefinition Document(long capturedMax) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [
            Slot("ownVac", 0L, 65535), Slot("ownOcc", 0L, 65535), Slot("otherVac", 0L, 65535),
            Slot("fromCell", 0L, 16), Slot("toCell", 0L, 16), Slot("capturedCell", 0L, capturedMax),
        ]),
        Rules = [
            new WorldRule(WorldCellName.Parse("masks"), [Set("ownVac", new WorldValueToken.Constant(1m)), Set("ownOcc", new WorldValueToken.Constant(4m))]),
            new WorldRule(WorldCellName.Parse("cells"), [Set("fromCell", Tz("ownVac")), Set("toCell", Tz("ownOcc")), Set("capturedCell", Tz("otherVac"))]),
        ],
    };

    [Fact]
    public void EveryEffectInARunEvaluatesItsOwnExpression() {
        using var fixture = Fixtures.FreshServer(definition: Document(capturedMax: 64));
        fixture.Step();

        Assert.Equal(0L, Value(fixture, "fromCell"));
        Assert.Equal(2L, Value(fixture, "toCell"));
        Assert.Equal(64L, Value(fixture, "capturedCell"));
        Assert.Empty(fixture.Server.RuleRuntimeDiagnostics());
    }

    [Fact]
    public void ALaterEffectRefusedByItsEnvelopeDropsTheWholeRunAndIsReported() {
        using var fixture = Fixtures.FreshServer(definition: Document(capturedMax: 16));
        fixture.Step();

        Assert.Equal(0L, Value(fixture, "toCell"));
        Assert.Equal(0L, Value(fixture, "capturedCell"));
        var diagnostic = Assert.Single(fixture.Server.RuleRuntimeDiagnostics());
        Assert.Equal(WorldRuleEffectRefusal.MutationRejected, diagnostic.Refusal);
        Assert.Equal("cells", diagnostic.Rule);
        Assert.Contains("capturedCell", diagnostic.Effect, StringComparison.Ordinal);
        Assert.Contains("capturedCell", diagnostic.Detail, StringComparison.Ordinal);
    }
}
