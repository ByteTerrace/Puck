using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Regression probes for behavior transitions in the completed state authoring implementation.</summary>
public sealed class StateCompletionReviewTests {
    [Fact]
    public void DisablingCosineCyclePreservesItsDisplayedValue() {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewCycle"),
            Kind: CellKind.Fixed,
            Cycle: new StateCycle(Output: CycleOutput.Cos),
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 0)]
        );
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [row]));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: row with { Cycle = null }
        ));
        fixture.Step();

        // At simulation tick zero cosine is exactly 1.0, regardless of the stored phase of zero.
        Assert.Equal(65536L, fixture.Server.Definition.State.Single().Cells!.Single().Value);
    }

    [Fact]
    public void ChangingRowDefaultDoesNotRestartAnExplicitCellOverride() {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewOverride"),
            Kind: CellKind.Int,
            Capacity: 8,
            Advance: new StateAdvance(PerSecondNumerator: 50400, PerSecondDenominator: 1),
            Cells: [new StateCell(
                Key: CellName.Parse(candidate: "a"),
                Value: 10,
                Advance: new StateAdvance(PerSecondNumerator: 100800, PerSecondDenominator: 1)
            )]
        );
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [row]));
        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: row with { Advance = new StateAdvance(PerSecondNumerator: 151200, PerSecondDenominator: 1) }
        ));
        fixture.Step();

        Assert.True(WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            rowName: "reviewOverride",
            key: "a",
            tick: fixture.Server.NextInputTick - 1,
            engineTick: fixture.Server.CompletedEngineTicks,
            row: out _, rawValue: out var actual, text: out _
        ));
        // The cell's own rate is two units per engine tick throughout the row-default edit.
        Assert.Equal(10L + 2L * (long)fixture.Server.CompletedEngineTicks, actual);
    }
}
