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
        Cells: cells ?? [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 100L))]
    );

    [Fact]
    public void AdvanceTrait_WithClockEpoch_ReadsBaseAtEpoch_AndAdvancesProportionally() {
        var advance = new StateAdvance(PerSecondNumerator: 10, PerSecondDenominator: 1);
        var clock = new StateCellClock(EpochEngineTick: 50400L);
        var cell = new StateCell(
            Key: StateRow.SlotKey,
            Value: CellValue.Int(value: 100L),
            Clock: clock
        );
        var row = CreateRow(advance: advance, cells: [cell]);

        // At epoch tick, reading cell must yield exactly base value
        StateReader.ReadCell(
            row: row,
            key: null,
            tick: 100UL,
            engineTick: 50400UL,
            rawValue: out var atEpoch,
            text: out _
        );
        Assert.Equal(100L, atEpoch);

        // One second later (50400 ticks later): should advance by exactly numerator (10)
        StateReader.ReadCell(
            row: row,
            key: null,
            tick: 100UL,
            engineTick: 100800UL,
            rawValue: out var afterOneSecond,
            text: out _
        );
        Assert.Equal(110L, afterOneSecond);
    }

    [Fact]
    public void AdvanceTrait_OptOutToNone_AlwaysReturnsBaseValue() {
        var advance = new StateAdvance(PerSecondNumerator: 10, PerSecondDenominator: 1);
        var clock = new StateCellClock(EpochEngineTick: 50400L);
        var cell = new StateCell(
            Key: CellName.Parse(candidate: "item"),
            Value: CellValue.Int(value: 100L),
            Behavior: StateCellBehavior.None,
            Clock: clock
        );
        var row = CreateRow(advance: advance, cells: [cell]);

        StateReader.ReadCell(
            row: row,
            key: "item",
            tick: 100UL,
            engineTick: 100800UL,
            rawValue: out var raw,
            text: out _
        );
        Assert.Equal(100L, raw);
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
            row: row,
            key: null,
            tick: 20UL,
            engineTick: 0UL,
            rawValue: out var atStart,
            text: out _
        );
        Assert.Equal(0L, atStart);

        // At tick 25, elapsed is 5 + 5 = 10 ticks -> exactly advances to step 1
        StateReader.ReadCell(
            row: row,
            key: null,
            tick: 25UL,
            engineTick: 0UL,
            rawValue: out var nextStep,
            text: out _
        );
        Assert.Equal(1L, nextStep);
    }

    [Fact]
    public void TransitionFromRowAdvanceToCellCycle_OverridesBehaviorAndClocks() {
        var rowAdvance = new StateAdvance(PerSecondNumerator: 10, PerSecondDenominator: 1);
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
        Assert.Null(effective.Advance);
        Assert.Same(cellCycle, effective.Cycle);

        // Reader applies cycle, not advance
        StateReader.ReadCell(
            row: row,
            key: "override",
            tick: 20UL,
            engineTick: 100800UL,
            rawValue: out var raw,
            text: out _
        );
        Assert.Equal(2L, raw); // 20 ticks with 10 ticks/step = step 2
    }

    [Fact]
    public void TransitionFromRowCycleToCellAdvance_OverridesBehaviorAndClocks() {
        var rowCycle = new StateCycle(TicksPerStep: 10);
        var cellAdvance = new StateAdvance(PerSecondNumerator: 5, PerSecondDenominator: 1);
        var clock = new StateCellClock(EpochTick: 100L, EpochEngineTick: 0L);

        var cell = new StateCell(
            Key: CellName.Parse(candidate: "override"),
            Value: CellValue.Int(value: 50L),
            Advance: cellAdvance,
            Clock: clock
        );
        var row = CreateRow(cycle: rowCycle, cells: [cell]);

        var effective = EffectiveBehavior.Resolve(cell: cell, row: row);
        Assert.Null(effective.Cycle);
        Assert.Same(cellAdvance, effective.Advance);

        // Reader applies advance, not cycle
        StateReader.ReadCell(
            row: row,
            key: "override",
            tick: 100UL,
            engineTick: 50400UL,
            rawValue: out var raw,
            text: out _
        );
        Assert.Equal(55L, raw); // 50 + 5
    }
}
