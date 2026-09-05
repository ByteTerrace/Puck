using Xunit;
using Puck.Physics.Motion;

namespace Puck.World.Tests;

/// <summary>Pins the engine-tick countdown boundary: authored duration storage is rate-independent, while consumption
/// uses each live step's width and treats the final partial step as a computed saturation rather than an explicit
/// negative write.</summary>
public sealed class WorldRuleCountdownLawTests {
    [Fact]
    public void CountdownUsesLiveStepWidthAndSaturatesFinalPartialStep() {
        var countdownName = CellName.Parse(candidate: "cooldown");
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: countdownName,
                    Kind: CellKind.Int,
                    NonNegative: true,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 1183L)])
            ]),
            Rules = [
                new WorldRule(
                    Name: CellName.Parse(candidate: "cooldown-tick"),
                    Gate: new ActionPredicate.CompareState(State: countdownName.Value, Comparison: ActionStateComparison.Greater, Value: 0m),
                    Effects: [new ActionEffect.CountdownState(State: countdownName.Value)])
            ],
        };

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step(stepTicks: 1120UL);
        Assert.Equal(expected: 63L, actual: CountdownValue(fixture: fixture, name: countdownName));

        fixture.Step(stepTicks: 1120UL);
        Assert.Equal(expected: 0L, actual: CountdownValue(fixture: fixture, name: countdownName));
    }
    [Fact]
    public void OutOfRangeValueSecondsRefusesByNameWithoutOverflow() {
        var denied = DurationDocument(seconds: decimal.MaxValue);
        var control = DurationDocument(seconds: 1m);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var reason));
        Assert.Contains(expectedSubstring: nameof(WorldRuleRefusal.DurationEngineTicksOutOfRange), actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: control, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static WorldDefinition DurationDocument(decimal seconds) {
        var countdownName = CellName.Parse(candidate: "cooldown");

        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: countdownName,
                    Kind: CellKind.Int,
                    NonNegative: true,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey)])
            ]),
            Rules = [
                new WorldRule(
                    Name: CellName.Parse(candidate: "arm-cooldown"),
                    Effects: [new ActionEffect.SetState(State: countdownName.Value, ValueSeconds: seconds)])
            ],
        };
    }
    private static long CountdownValue(WorldFixture fixture, CellName name) =>
        fixture.Server.Definition.State.Single(predicate: row => (row.Name == name)).Cells!.Single().Value;
}
