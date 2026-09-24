using Xunit;

namespace Puck.State.Tests;

/// <summary>Guards value, velocity, and phase calculations across trait transitions and clocks.</summary>
public sealed class StateCellClockTransitionLawTests {
    private static StateRow CreateRow(
        CellKind kind = CellKind.Int,
        StateAdvance? advance = null,
        StateDynamics? dynamics = null,
        StateCycle? cycle = null,
        StateCell[]? cells = null
    ) => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: kind,
        Advance: advance,
        Dynamics: dynamics,
        Cycle: cycle,
        Cells: (cells ?? [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 100L))])
    );

    [Fact]
    public void AdvanceTrait_WithClockEpoch_ReadsBaseAtEpoch_AndAdvancesProportionally() {
        var advance = new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 10);
        var clock = new StateCellClock(EpochEngineTick: 50400L);
        var cell = new StateCell(
            Key: StateRow.SlotKey,
            Value: CellValue.Int(value: 100L),
            Clock: clock
        );
        var row = CreateRow(advance: advance, cells: [cell]);

        // At epoch tick, reading cell must yield exactly base value
        StateReader.ReadCell(
            engineTick: 50400UL,
            key: null,
            rawValue: out var atEpoch,
            row: row,
            text: out _,
            tick: 100UL
        );
        Assert.Equal(actual: atEpoch, expected: 100L);

        // One second later (50400 ticks later): should advance by exactly numerator (10)
        StateReader.ReadCell(
            engineTick: 100800UL,
            key: null,
            rawValue: out var afterOneSecond,
            row: row,
            text: out _,
            tick: 100UL
        );
        Assert.Equal(actual: afterOneSecond, expected: 110L);
    }
    [Fact]
    public void AdvanceTrait_OptOutToNone_AlwaysReturnsBaseValue() {
        var advance = new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 10);
        var clock = new StateCellClock(EpochEngineTick: 50400L);
        var cell = new StateCell(
            Key: CellName.Parse(candidate: "item"),
            Value: CellValue.Int(value: 100L),
            Behavior: StateCellBehavior.None,
            Clock: clock
        );
        var row = CreateRow(advance: advance, cells: [cell]);

        StateReader.ReadCell(
            engineTick: 100800UL,
            key: "item",
            rawValue: out var raw,
            row: row,
            text: out _,
            tick: 100UL
        );
        Assert.Equal(actual: raw, expected: 100L);
    }
    [Fact]
    public void CycleTrait_WithClockEpochAndSubstep_RespectsOffset() {
        var cycle = new StateCycle(TicksPerStep: 10);
        var clock = new StateCellClock(EpochTick: 20L, SubstepTicks: 5L);
        var cell = new StateCell(
            Key: StateRow.SlotKey,
            Value: CellValue.Int(value: 0L),
            Clock: clock
        );
        var row = CreateRow(cycle: cycle, cells: [cell]);

        // At tick 20 with SubstepTicks 5, elapsed is 5 ticks (step 0)
        StateReader.ReadCell(
            engineTick: 0UL,
            key: null,
            rawValue: out var atStart,
            row: row,
            text: out _,
            tick: 20UL
        );
        Assert.Equal(actual: atStart, expected: 0L);

        // At tick 25, elapsed is 5 + 5 = 10 ticks -> exactly advances to step 1
        StateReader.ReadCell(
            engineTick: 0UL,
            key: null,
            rawValue: out var nextStep,
            row: row,
            text: out _,
            tick: 25UL
        );
        Assert.Equal(actual: nextStep, expected: 1L);
    }
    [Fact]
    public void TransitionFromRowAdvanceToCellCycle_OverridesBehaviorAndClocks() {
        var rowAdvance = new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 10);
        var cellCycle = new StateCycle(TicksPerStep: 10);
        var clock = new StateCellClock(EpochTick: 0L, EpochEngineTick: 50400L, SubstepTicks: 0L);

        var cell = new StateCell(
            Key: CellName.Parse(candidate: "override"),
            Value: CellValue.Int(value: 0L),
            Cycle: cellCycle,
            Clock: clock
        );
        var row = CreateRow(advance: rowAdvance, cells: [cell]);

        var effective = EffectiveBehavior.Resolve(cell: cell, row: row);

        Assert.Null(@object: effective.Advance);
        Assert.Same(cellCycle, effective.Cycle);

        // Reader applies cycle, not advance
        StateReader.ReadCell(
            engineTick: 100800UL,
            key: "override",
            rawValue: out var raw,
            row: row,
            text: out _,
            tick: 20UL
        );
        Assert.Equal(actual: raw, expected: 2L); // 20 ticks with 10 ticks/step = step 2
    }
    [Fact]
    public void TransitionFromRowCycleToCellAdvance_OverridesBehaviorAndClocks() {
        var rowCycle = new StateCycle(TicksPerStep: 10);
        var cellAdvance = new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 5);
        var clock = new StateCellClock(EpochTick: 100L, EpochEngineTick: 0L);

        var cell = new StateCell(
            Key: CellName.Parse(candidate: "override"),
            Value: CellValue.Int(value: 50L),
            Advance: cellAdvance,
            Clock: clock
        );
        var row = CreateRow(cycle: rowCycle, cells: [cell]);

        var effective = EffectiveBehavior.Resolve(cell: cell, row: row);

        Assert.Null(@object: effective.Cycle);
        Assert.Same(cellAdvance, effective.Advance);

        // Reader applies advance, not cycle
        StateReader.ReadCell(
            engineTick: 50400UL,
            key: "override",
            rawValue: out var raw,
            row: row,
            text: out _,
            tick: 100UL
        );
        Assert.Equal(actual: raw, expected: 55L); // 50 + 5
    }
}
