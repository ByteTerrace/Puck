using Xunit;

namespace Puck.World.Tests;

/// <summary>Every top-level state effect in a rule evaluates its own expression and is its own boundary: a later
/// effect refused by its destination's envelope leaves the earlier writes applied and is reported by rule and effect.
/// Only a transaction groups effects atomically.</summary>
public sealed class WorldRuleEffectRunLawTests {
    private static WorldStateRow Slot(string name, long value, long max) =>
        new(CellName.Parse(name), CellKind.Int, Min: 0, Max: max, Cells: [new WorldStateCell(WorldStateRow.SlotKey, value)]);
    private static ActionEffect.SetState Set(string state, params ValueToken[] tokens) => new(State: state, Expression: new ValueExpression(tokens));
    private static WorldTransactionStep.SetCell Step(string state, params ValueToken[] tokens) => new(State: state, Expression: new ValueExpression(tokens));
    private static ValueToken[] Tz(string row) => [new ValueToken.State(row), new ValueToken.TrailingZeroCount()];
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;

    private static WorldDefinition Document(long capturedMax, bool asTransaction) {
        ActionEffect[] writes = [Set("fromCell", Tz("ownVac")), Set("toCell", Tz("ownOcc")), Set("capturedCell", Tz("otherVac"))];
        WorldTransactionStep[] steps = [Step("fromCell", Tz("ownVac")), Step("toCell", Tz("ownOcc")), Step("capturedCell", Tz("otherVac"))];
        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                Slot("ownVac", 0L, 65535), Slot("ownOcc", 0L, 65535), Slot("otherVac", 0L, 65535),
                Slot("fromCell", 0L, 16), Slot("toCell", 0L, 16), Slot("capturedCell", 0L, capturedMax),
            ]),
            Rules = [
                new WorldRule(CellName.Parse("masks"), [Set("ownVac", new ValueToken.Constant(1m)), Set("ownOcc", new ValueToken.Constant(4m))]),
                new WorldRule(CellName.Parse("cells"), asTransaction ? [new ActionEffect.Transaction(Effects: steps)] : writes),
            ],
        };
    }

    [Fact]
    public void EveryEffectEvaluatesItsOwnExpression() {
        using var fixture = Fixtures.FreshServer(definition: Document(capturedMax: 64, asTransaction: false));
        fixture.Step();

        Assert.Equal(0L, Value(fixture, "fromCell"));
        Assert.Equal(2L, Value(fixture, "toCell"));
        Assert.Equal(64L, Value(fixture, "capturedCell"));
        Assert.Empty(fixture.Server.RuleRuntimeDiagnostics());
    }

    [Fact]
    public void ALaterRefusedEffectLeavesEarlierWritesAppliedAndIsReported() {
        using var fixture = Fixtures.FreshServer(definition: Document(capturedMax: 16, asTransaction: false));
        fixture.Step();

        Assert.Equal(2L, Value(fixture, "toCell"));
        Assert.Equal(0L, Value(fixture, "capturedCell"));
        var diagnostic = Assert.Single(fixture.Server.RuleRuntimeDiagnostics());
        Assert.Equal(WorldRuleEffectRefusal.MutationRejected, diagnostic.Refusal);
        Assert.Equal("cells", diagnostic.Rule);
        Assert.Contains("capturedCell", diagnostic.Effect, StringComparison.Ordinal);
    }

    [Fact]
    public void ATransactionIsTheOneAtomicGroup() {
        using var fixture = Fixtures.FreshServer(definition: Document(capturedMax: 16, asTransaction: true));
        fixture.Step();

        Assert.Equal(0L, Value(fixture, "toCell"));
        Assert.Equal(0L, Value(fixture, "capturedCell"));
        Assert.NotEmpty(fixture.Server.RuleRuntimeDiagnostics());
    }
}
