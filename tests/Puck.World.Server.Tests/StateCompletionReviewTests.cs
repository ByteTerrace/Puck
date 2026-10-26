using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Regression probes for behavior transitions in the completed state authoring implementation.</summary>
public sealed class StateCompletionReviewTests {
    [Fact]
    public void DisablingCosineCyclePreservesItsDisplayedValue() {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewCycle"),
            Kind: CellKind.Fixed,
            Cycle: new StateCycle(Output: CycleOutput.Cos),
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Fixed(rawBits: 0L))]
        );
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [row]));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: row with { Cycle = null }
        ));
        fixture.Step();

        // At simulation tick zero cosine is exactly 1.0, regardless of the stored phase of zero.
        Assert.Equal(65536L, fixture.Server.ExportDefinition().State.Single().Cells!.Single().Value.Raw);
    }
    [Fact]
    public void ChangingRowDefaultDoesNotRestartAnExplicitCellOverride() {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewOverride"),
            Kind: CellKind.Int,
            Capacity: 8,
            Advance: new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 50400),
            Cells: [new StateCell(
                Key: CellName.Parse(candidate: "a"),
                Value: CellValue.Int(value: 10L),
                Advance: new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 100800)
            )]
        );
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [row]));

        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: row with { Advance = new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 151200) }
        ));
        fixture.Step();

        Assert.True(condition: WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            rowName: "reviewOverride",
            key: "a",
            tick: (fixture.Server.NextInputTick - 1),
            engineTick: fixture.Server.CompletedEngineTicks,
            row: out _, rawValue: out var actual, text: out _
        ));
        // The cell's own rate is two units per engine tick throughout the row-default edit.
        Assert.Equal((10L + (2L * ((long)fixture.Server.CompletedEngineTicks))), actual);
    }
    [Fact]
    public void SameDeclarationUpsertStateRowReplacesSlotRingAndOrderedRowsInTheArena() {
        var poolRow = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewPool"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "a"), Value: CellValue.Int(value: 1L)),
                new StateCell(Key: CellName.Parse(candidate: "b"), Value: CellValue.Int(value: 2L)),
            ]
        );
        var slotRow = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewSlot"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 42L))]
        );
        var ringRow = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewRing"),
            Kind: CellKind.Int,
            Domain: new StateDomain.Ring(Capacity: 4),
            HistoryCursor: 2,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "0"), Value: CellValue.Int(value: 10L)),
                new StateCell(Key: CellName.Parse(candidate: "1"), Value: CellValue.Int(value: 20L)),
            ]
        );
        var orderedRow = new WorldStateRow(
            Name: CellName.Parse(candidate: "reviewOrder"),
            Kind: CellKind.Bool,
            Domain: new StateDomain.KeysOf(Row: CellName.Parse(candidate: "reviewPool"), Ordered: true),
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "a"), Value: CellValue.Bool(value: true)),
                new StateCell(Key: CellName.Parse(candidate: "b"), Value: CellValue.Bool(value: true)),
            ]
        );
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [poolRow, slotRow, ringRow, orderedRow]));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: slotRow with { Cells = [] }
        ));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: ringRow with {
                HistoryCursor = 1,
                Cells = [new StateCell(Key: CellName.Parse(candidate: "0"), Value: CellValue.Int(value: 99L))],
            }
        ));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: orderedRow with {
                Cells = [
                    new StateCell(Key: CellName.Parse(candidate: "b"), Value: CellValue.Bool(value: true)),
                    new StateCell(Key: CellName.Parse(candidate: "a"), Value: CellValue.Bool(value: true)),
                ],
            }
        ));
        fixture.Step();

        var exported = fixture.Server.ExportDefinition().State.ToDictionary(keySelector: static row => row.Name.Value);

        Assert.Empty(collection: exported["reviewSlot"].Cells ?? []);
        Assert.Equal(1, exported["reviewRing"].HistoryCursor);
        Assert.Equal(99L, Assert.Single(collection: exported["reviewRing"].Cells!).Value.AsInt);
        Assert.Equal(["b", "a"], exported["reviewOrder"].Cells!.Select(selector: static cell => cell.Key.Value));
    }
}
