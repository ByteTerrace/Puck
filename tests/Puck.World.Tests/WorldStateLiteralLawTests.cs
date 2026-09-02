using Xunit;

namespace Puck.World.Tests;

/// <summary>State-rule literals retain their authored decimal value through compilation. Integer gates and writes
/// use the full signed-64-bit carrier instead of narrowing through binary32 or Q48.16.</summary>
public sealed class WorldStateLiteralLawTests {
    private const long BeyondBinary32ExactInteger = 16_777_217L;

    [Fact]
    public void IntegerComparisonAndWrite_PreserveAValueBeyondBinary32Precision() {
        var sourceName = WorldCellName.Parse(candidate: "exact-source");
        var destinationName = WorldCellName.Parse(candidate: "exact-destination");
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: sourceName,
                    Kind: CellKind.Int,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: BeyondBinary32ExactInteger)]
                ),
                new WorldStateRow(
                    Name: destinationName,
                    Kind: CellKind.Int,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
                ),
            ]),
            Rules = [new WorldRule(
                Name: WorldCellName.Parse(candidate: "exact-literal"),
                Gate: new ActionPredicate.CompareState(
                    State: sourceName.Value,
                    Comparison: Puck.Physics.Motion.ActionStateComparison.Equal,
                    Value: BeyondBinary32ExactInteger
                ),
                Effects: [new ActionEffect.SetState(
                    State: destinationName.Value,
                    Value: BeyondBinary32ExactInteger
                )]
            )],
        };

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            expected: BeyondBinary32ExactInteger,
            actual: fixture.Server.Definition.State.Single(row => row.Name == destinationName).Cells!.Single().Value
        );
    }

    [Fact]
    public void IntegerComparison_PreservesLowBitsAcrossTheDeclaredIntegerRange() {
        const long Value = (1L << 40) + 1L;
        var sourceName = WorldCellName.Parse(candidate: "wide-source");
        var destinationName = WorldCellName.Parse(candidate: "wide-result");
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: sourceName, Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: Value)]),
                new WorldStateRow(Name: destinationName, Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)]),
            ]),
            Rules = [new WorldRule(
                Name: WorldCellName.Parse(candidate: "wide-literal"),
                Gate: new ActionPredicate.CompareState(State: sourceName.Value, Comparison: Puck.Physics.Motion.ActionStateComparison.Equal, Value: Value),
                Effects: [new ActionEffect.SetState(State: destinationName.Value, Value: 1m)]
            )],
        };

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        Assert.Equal(expected: 1L, actual: fixture.Server.Definition.State.Single(row => row.Name == destinationName).Cells!.Single().Value);
    }

    [Fact]
    public void ConsecutiveStateEffects_AreAtomicWhenALaterWriteRefuses() {
        var first = WorldCellName.Parse(candidate: "atomic-first");
        var second = WorldCellName.Parse(candidate: "atomic-second");
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: first, Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)]),
                new WorldStateRow(Name: second, Kind: CellKind.Int, Min: 0L, Max: 1L, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)]),
            ]),
            Rules = [new WorldRule(
                Name: WorldCellName.Parse(candidate: "atomic-refusal"),
                Gate: null,
                Effects: [
                    new ActionEffect.SetState(State: first.Value, Value: 1m),
                    new ActionEffect.SetState(State: second.Value, Value: 2m),
                ]
            )],
        };

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        Assert.All(
            collection: fixture.Server.Definition.State,
            action: static row => Assert.Equal(expected: 0L, actual: row.Cells!.Single().Value)
        );
    }

    [Fact]
    public void IntegerLiteralOutsideTheStateCarrier_RefusesByRuleName() {
        var name = WorldCellName.Parse(candidate: "bounded-int");
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: name,
                Kind: CellKind.Int,
                Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
            )]),
        };
        var rule = new WorldRule(
            Name: WorldCellName.Parse(candidate: "oversized-literal"),
            Gate: new ActionPredicate.CompareState(
                State: name.Value,
                Comparison: Puck.Physics.Motion.ActionStateComparison.Equal,
                Value: decimal.MaxValue
            ),
            Effects: []
        );

        var exception = Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.Compile(
            definition: definition,
            rule: rule
        ));

        Assert.Equal(expected: WorldRuleRefusal.StateCellUnaddressable, actual: exception.Refusal);
        Assert.Contains(expectedSubstring: "oversized-literal", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
}
